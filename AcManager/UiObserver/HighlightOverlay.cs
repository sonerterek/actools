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
            // Clear any existing debug shapes
            ClearDebugRects();

            // Ensure overlay covers virtual screen in DIP coordinates
            SetWindowToVirtualScreenInDip();
            EnsureVisible();

            // Draw leaf elements in Orange with 2px inset so they're visible inside group boundaries
            if (leafRectsInDip != null) {
                foreach (var rectDip in leafRectsInDip) {
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
                }
            }
            
            // Draw group elements in Gray at full size (no inset)
            if (groupRectsInDip != null) {
                foreach (var rectDip in groupRectsInDip) {
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
                }
            }
        }

        /// <summary>
        /// Clears all debug rectangles (keeps focus rectangle visible if shown).
        /// </summary>
        public void ClearDebugRects() {
            foreach (var shape in _debugShapes) {
                _canvas.Children.Remove(shape);
            }
            _debugShapes.Clear();
            
            // If no focus rect is shown, hide the overlay
            if (_focusRect.Visibility == Visibility.Collapsed) {
                try { if (IsVisible) Hide(); } catch { }
            }
        }

        /// <summary>
        /// Ensures the overlay window is visible.
        /// </summary>
        private void EnsureVisible() {
            try {
                Topmost = true;
                if (!IsVisible) Show();
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
