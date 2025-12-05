using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AcManager.UiObserver {
    // Lightweight topmost transparent overlay that draws a list of rectangles (in device-independent pixels DIP).
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

            // Cover the virtual screen in DIP coordinates. SystemParameters often reports device (physical) pixels,
            // so convert them to DIP using a PresentationSource transform if available.
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

        // rectsInDip: list of rectangles in DIP coordinates relative to virtual screen origin
        public void ShowRects(IEnumerable<Rect> rectsInDip, Matrix? transformFromDeviceToDip = null) {
            _canvas.Children.Clear();
            if (rectsInDip == null) {
                if (IsVisible) Hide();
                return;
            }

            // Ensure overlay covers virtual screen in DIP coordinates
            SetWindowToVirtualScreenInDip();

            // If possible, keep Owner set to main window so overlay inherits the same DPI context
            try {
                if (Application.Current?.MainWindow != null) Owner = Application.Current.MainWindow;
            } catch { }

            foreach (var rectDip in rectsInDip) {
                // Skip degenerate
                if (double.IsNaN(rectDip.Width) || double.IsNaN(rectDip.Height)) continue;
                if (rectDip.Width < 1.0 || rectDip.Height < 1.0) continue;

                var shape = new Rectangle {
                    Width = rectDip.Width,
                    Height = rectDip.Height,
                    Stroke = Brushes.Orange,
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    IsHitTestVisible = false
                };
                // rectDip is already in DIP; Left/Top are set in DIP as well
                Canvas.SetLeft(shape, rectDip.Left - Left);
                Canvas.SetTop(shape, rectDip.Top - Top);
                _canvas.Children.Add(shape);
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
