using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AcManager.UiObserver {
    // Lightweight topmost transparent overlay that draws a list of rectangles (in device-independent pixels DIP).
    // This is a debug visualization tool: show once, then clear. No continuous updates needed.
    internal class HighlightOverlay : Window {
        private readonly Canvas _canvas;

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
        /// Shows rectangles for debug visualization. This is a one-time snapshot.
        /// leafRectsInDip: leaf rectangles in DIP (Orange, inset by 2px to be visible inside groups)
        /// groupRectsInDip: group rectangles in DIP (Gray, full size)
        /// </summary>
        public void ShowRects(IEnumerable<Rect> leafRectsInDip, IEnumerable<Rect> groupRectsInDip = null) {
            _canvas.Children.Clear();

            // Ensure overlay covers virtual screen in DIP coordinates
            SetWindowToVirtualScreenInDip();

            // If possible, keep Owner set to main window so overlay inherits the same DPI context
            try {
                if (Application.Current?.MainWindow != null) Owner = Application.Current.MainWindow;
            } catch { }

            bool hasAny = false;

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
                    // Position using inset coordinates
                    Canvas.SetLeft(shape, insetRect.Left - Left);
                    Canvas.SetTop(shape, insetRect.Top - Top);
                    _canvas.Children.Add(shape);
                    hasAny = true;
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
                    // rectDip is already in DIP; Left/Top are set in DIP as well
                    Canvas.SetLeft(shape, rectDip.Left - Left);
                    Canvas.SetTop(shape, rectDip.Top - Top);
                    _canvas.Children.Add(shape);
                    hasAny = true;
                }
            }

            if (!hasAny) {
                if (IsVisible) Hide();
                return;
            }

            try {
                Topmost = true;
                if (!IsVisible) Show();
            } catch { }
        }

        public void HideOverlay() {
            _canvas.Children.Clear();
            try { if (IsVisible) Hide(); } catch { }
        }
    }
}
