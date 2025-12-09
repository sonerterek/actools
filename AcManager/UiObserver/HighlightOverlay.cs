using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AcManager.UiObserver {
    /// <summary>
    /// Persistent topmost transparent overlay for navigation feedback.
    /// - Always shows the focused node with a highlighted rectangle
    /// - Can toggle debug mode to show all navigable elements
    /// </summary>
    internal class HighlightOverlay : Window {
        private readonly Canvas _canvas;
        private Rectangle _focusRect;
        private readonly List<Shape> _debugShapes = new List<Shape>();
        
        // ? NEW: Track disposal state
        private bool _isDisposed = false;

        // ? NEW: Limit maximum debug shapes to prevent OOM
        private const int MAX_DEBUG_SHAPES = 500;

        public HighlightOverlay() {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            WindowStyle = WindowStyle.None;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            IsHitTestVisible = false;
            ShowActivated = false;
            Focusable = false;
            WindowStartupLocation = WindowStartupLocation.Manual;

            // Try to inherit DPI context from main window to avoid unexpected scaling
            try {
                if (Application.Current?.MainWindow != null) {
                    Owner = Application.Current.MainWindow;
                }
            } catch { }

            // Cover the virtual screen in DIP coordinates
            SetWindowToVirtualScreenInDip();

            _canvas = new Canvas { IsHitTestVisible = false, Background = Brushes.Transparent };
            Content = _canvas;

            // Create the persistent focus rectangle (initially hidden)
            _focusRect = new Rectangle {
                Stroke = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215)), // Windows blue
                StrokeThickness = 3,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            _canvas.Children.Add(_focusRect);

            // ? NEW: Hook Closed event to ensure cleanup
            Closed += (s, e) => Dispose();
        }

        // Convert system virtual screen values (usually in device pixels) to DIP and assign to window bounds.
        private void SetWindowToVirtualScreenInDip() {
            // Default: use raw SystemParameters values (fallback)
            var leftDevice = SystemParameters.VirtualScreenLeft;
            var topDevice = SystemParameters.VirtualScreenTop;
            var rightDevice = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            var bottomDevice = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

            // Try to obtain a PresentationSource from Owner or main window to get the device->DIP transform
            Matrix transform = Matrix.Identity;
            try {
                var ownerWin = Owner ?? Application.Current?.MainWindow;
                if (ownerWin != null) {
                    var ps = PresentationSource.FromVisual(ownerWin);
                    if (ps?.CompositionTarget != null) {
                        transform = ps.CompositionTarget.TransformFromDevice;
                    }
                }
            } catch { /* ignore and use identity */ }

            var tlDip = transform.Transform(new Point(leftDevice, topDevice));
            var brDip = transform.Transform(new Point(rightDevice, bottomDevice));

            Left = tlDip.X;
            Top = tlDip.Y;
            Width = Math.Max(0, brDip.X - tlDip.X);
            Height = Math.Max(0, brDip.Y - tlDip.Y);
        }

        /// <summary>
        /// Shows the focused node rectangle at the specified position.
        /// </summary>
        /// <param name="rectDip">Rectangle in DIP coordinates for the focused node</param>
        public void ShowFocusRect(Rect rectDip) {
            // ? NEW: Check if disposed
            if (_isDisposed || _focusRect == null) return;
            
            // Ensure overlay is shown and positioned correctly
            EnsureVisible();

            // Skip degenerate rects
            if (double.IsNaN(rectDip.Width) || double.IsNaN(rectDip.Height) || 
                rectDip.Width < 1.0 || rectDip.Height < 1.0) {
                HideFocusRect();
                return;
            }

            _focusRect.Width = rectDip.Width;
            _focusRect.Height = rectDip.Height;
            Canvas.SetLeft(_focusRect, rectDip.Left - Left);
            Canvas.SetTop(_focusRect, rectDip.Top - Top);
            _focusRect.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Hides the focused node rectangle.
        /// </summary>
        public void HideFocusRect() {
            // ? NEW: Check if disposed
            if (_isDisposed || _focusRect == null) return;
            
            _focusRect.Visibility = Visibility.Collapsed;
            
            // If no debug shapes are shown, hide the overlay window
            if (_debugShapes.Count == 0) {
                try { if (IsVisible) Hide(); } catch { }
            }
        }

        /// <summary>
        /// Shows debug rectangles for all navigable elements.
        /// leafRectsInDip: leaf rectangles in DIP (Orange, inset by 2px to be visible inside groups)
        /// groupRectsInDip: group rectangles in DIP (Gray, full size)
        /// </summary>
        public void ShowDebugRects(IEnumerable<Rect> leafRectsInDip, IEnumerable<Rect> groupRectsInDip = null) {
            // ? Check if disposed first
            if (_isDisposed) return;
            
            // ? MEMORY DEBUG: Track before clearing
            var beforeShapes = _debugShapes.Count;
            var beforeCanvasChildren = _canvas.Children.Count;
            
            // ? Clear aggressively to free memory ASAP (ONLY ONE CALL!)
            ClearDebugRectsAggressive();
            
            // ? MEMORY DEBUG: Track after clearing
            System.Diagnostics.Debug.WriteLine($"[HighlightOverlay] BEFORE clear: {beforeShapes} shapes, {beforeCanvasChildren} canvas children");
            System.Diagnostics.Debug.WriteLine($"[HighlightOverlay] AFTER clear: {_debugShapes.Count} shapes, {_canvas.Children.Count} canvas children");

            // Ensure overlay covers virtual screen in DIP coordinates
            SetWindowToVirtualScreenInDip();
            EnsureVisible();

            int shapeCount = 0;
            int leafCount = 0;
            int groupCount = 0;

            // Draw leaf elements in Orange with 2px inset so they're visible inside group boundaries
            if (leafRectsInDip != null) {
                foreach (var rectDip in leafRectsInDip) {
                    // ? Prevent OOM by limiting shape count
                    if (shapeCount >= MAX_DEBUG_SHAPES) break;

                    // Skip degenerate
                    if (double.IsNaN(rectDip.Width) || double.IsNaN(rectDip.Height)) continue;
                    if (rectDip.Width < 1.0 || rectDip.Height < 1.0) continue;

                    // Inset by 2 DIP pixels on all sides so leaf boundaries are visible inside groups
                    const double inset = 2.0;
                    var insetRect = new Rect(
                        rectDip.Left + inset,
                        rectDip.Top + inset,
                        Math.Max(0, rectDip.Width - inset * 2),
                        Math.Max(0, rectDip.Height - inset * 2)
                    );

                    // Skip if inset makes it too small
                    if (insetRect.Width < 1.0 || insetRect.Height < 1.0) continue;

                    var shape = new Rectangle {
                        Width = insetRect.Width,
                        Height = insetRect.Height,
                        Stroke = Brushes.Orange,
                        StrokeThickness = 2,
                        Fill = Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(shape, insetRect.Left - Left);
                    Canvas.SetTop(shape, insetRect.Top - Top);
                    _canvas.Children.Add(shape);
                    _debugShapes.Add(shape);
                    shapeCount++;
                    leafCount++;
                }
            }
            
            // Draw group elements in Gray at full size (no inset)
            if (groupRectsInDip != null) {
                foreach (var rectDip in groupRectsInDip) {
                    // ? Prevent OOM by limiting shape count
                    if (shapeCount >= MAX_DEBUG_SHAPES) break;

                    // Skip degenerate
                    if (double.IsNaN(rectDip.Width) || double.IsNaN(rectDip.Height)) continue;
                    if (rectDip.Width < 1.0 || rectDip.Height < 1.0) continue;

                    var shape = new Rectangle {
                        Width = rectDip.Width,
                        Height = rectDip.Height,
                        Stroke = Brushes.Gray,
                        StrokeThickness = 2,
                        Fill = Brushes.Transparent,
                        IsHitTestVisible = false
                    };
                    Canvas.SetLeft(shape, rectDip.Left - Left);
                    Canvas.SetTop(shape, rectDip.Top - Top);
                    _canvas.Children.Add(shape);
                    _debugShapes.Add(shape);
                    shapeCount++;
                    groupCount++;
                }
            }

            // ? MEMORY DEBUG: Report final state
            System.Diagnostics.Debug.WriteLine($"[HighlightOverlay] CREATED: {leafCount} leaf shapes (orange), {groupCount} group shapes (gray), TOTAL: {shapeCount}");
            System.Diagnostics.Debug.WriteLine($"[HighlightOverlay] FINAL: {_debugShapes.Count} in list, {_canvas.Children.Count} in canvas");

            // ? If we hit the limit, warn in debug output
            if (shapeCount >= MAX_DEBUG_SHAPES) {
                System.Diagnostics.Debug.WriteLine($"[HighlightOverlay] WARNING: Hit shape limit ({MAX_DEBUG_SHAPES}). Some elements not visualized.");
            }
        }

        /// <summary>
        /// Clears all debug rectangles (keeps focus rectangle visible if shown).
        /// </summary>
        public void ClearDebugRects() {
            if (_isDisposed) return;
            
            foreach (var shape in _debugShapes) {
                _canvas.Children.Remove(shape);
            }
            _debugShapes.Clear();
            
            // If no focus rect is shown, hide the overlay
            if (_focusRect != null && _focusRect.Visibility == Visibility.Collapsed) {
                try { if (IsVisible) Hide(); } catch { }
            }
        }

        /// <summary>
        /// ? Aggressive cleanup with immediate memory release
        /// </summary>
        private void ClearDebugRectsAggressive() {
            if (_isDisposed) return;
            
            // Remove from canvas first
            foreach (var shape in _debugShapes) {
                _canvas.Children.Remove(shape);
            }
            
            // Clear list
            _debugShapes.Clear();
            
            // ? Trim canvas children to reduce memory
            if (_canvas.Children.Count > 100 && _focusRect != null) {
                // Keep only focus rect
                var focusRectIndex = _canvas.Children.IndexOf(_focusRect);
                for (int i = _canvas.Children.Count - 1; i >= 0; i--) {
                    if (i != focusRectIndex) {
                        _canvas.Children.RemoveAt(i);
                    }
                }
            }
            
            // If no focus rect is shown, hide the overlay
            if (_focusRect != null && _focusRect.Visibility == Visibility.Collapsed) {
                try { if (IsVisible) Hide(); } catch { }
            }
        }

        /// <summary>
        /// ? NEW: Proper disposal to prevent memory leaks and GC deadlocks
        /// </summary>
        public void Dispose() {
            if (_isDisposed) return;
            _isDisposed = true;
            
            // ? CRITICAL: Prevent Window finalizer from running during GC
            // Without this, Gen 2 GC triggers finalizers that try to use Dispatcher,
            // causing circular wait deadlock: GC waits for finalizers, finalizers wait for Dispatcher, Dispatcher blocked by GC
            GC.SuppressFinalize(this);
            
            try {
                // Clear all shapes
                ClearDebugRectsAggressive();
                
                // Remove focus rect
                if (_focusRect != null) {
                    _canvas.Children.Remove(_focusRect);
                    _focusRect = null;
                }
                
                // Clear canvas
                _canvas.Children.Clear();
                
                // ? FIX: Defer Close() to avoid WPF rendering deadlock during modal dialog disposal
                // Synchronous Close() can block on MediaContext.CompleteRender() when called during modal event handling
                if (IsVisible) {
                    Hide(); // Hide immediately (synchronous, but fast)
                    
                    // Close async to avoid deadlock
                    Dispatcher.BeginInvoke(new Action(() => {
                        try {
                            Close();
                        } catch { }
                    }), System.Windows.Threading.DispatcherPriority.Background);
                }
            } catch { }
        }

        /// <summary>
        /// Ensures the overlay window is visible.
        /// </summary>
        private void EnsureVisible() {
            if (_isDisposed) return;
            
            try {
                Topmost = true;
                if (!IsVisible) {
                    // ? FIX: Defer Show() to avoid WPF rendering deadlock when called during modal dialog event handling.
                    // Synchronous Show() can block on MediaContext.CompleteRender() when a modal dialog is active.
                    Dispatcher.BeginInvoke(new Action(() => {
                        if (!_isDisposed && !IsVisible) {
                            Show();
                        }
                    }), System.Windows.Threading.DispatcherPriority.Loaded);
                }
            } catch { }
        }

        /// <summary>
        /// Legacy method for backward compatibility - shows debug rects.
        /// </summary>
        [Obsolete("Use ShowDebugRects instead")]
        public void ShowRects(IEnumerable<Rect> leafRectsInDip, IEnumerable<Rect> groupRectsInDip = null) {
            ShowDebugRects(leafRectsInDip, groupRectsInDip);
        }

        /// <summary>
        /// Legacy method for backward compatibility - clears debug rects and hides focus rect.
        /// </summary>
        [Obsolete("Use ClearDebugRects or HideFocusRect instead")]
        public void HideOverlay() {
            ClearDebugRects();
            HideFocusRect();
        }
    }
}
