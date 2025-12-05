using System;
using System.Windows;
using System.Windows.Media;

namespace AcManager.UiObserver
{
    // Represents one navigation candidate (one visual chosen to represent a logical element)
    internal class NavElem
    {
        public WeakReference<FrameworkElement> VisualRef { get; }
        public object LogicalKey { get; }
        public PresentationSource PresentationSource { get; private set; }
        public Rect BoundsDip { get; private set; } // normalized to device-independent pixels (DIP / WPF units)
        public string Id { get; } // optional stable id (Name / AutomationId / fallback)

        public NavElem(FrameworkElement fe, object logicalKey, string id)
        {
            VisualRef = new WeakReference<FrameworkElement>(fe);
            LogicalKey = logicalKey;
            Id = id;
            PresentationSource = PresentationSource.FromVisual(fe);
            UpdateBounds();
        }

        public bool TryGetVisual(out FrameworkElement fe)
        {
            return VisualRef.TryGetTarget(out fe);
        }

        public void UpdateBounds()
        {
            if (!VisualRef.TryGetTarget(out var fe)) {
                BoundsDip = Rect.Empty;
                PresentationSource = null;
                return;
            }

            PresentationSource = PresentationSource.FromVisual(fe);
            if (PresentationSource == null || !fe.IsVisible) {
                BoundsDip = Rect.Empty;
                return;
            }

            try {
                // PointToScreen returns screen coordinates in DIP
                var topLeftDip = fe.PointToScreen(new Point(0, 0));
                var bottomRightDip = fe.PointToScreen(new Point(fe.ActualWidth, fe.ActualHeight));
                var x1 = Math.Min(topLeftDip.X, bottomRightDip.X);
                var y1 = Math.Min(topLeftDip.Y, bottomRightDip.Y);
                var x2 = Math.Max(topLeftDip.X, bottomRightDip.X);
                var y2 = Math.Max(topLeftDip.Y, bottomRightDip.Y);

                BoundsDip = new Rect(new Point(x1, y1), new Point(x2, y2));
            } catch {
                BoundsDip = Rect.Empty;
            }
        }

        public Point? CenterDip {
            get {
                if (BoundsDip.IsEmpty) return null;
                return new Point(BoundsDip.Left + BoundsDip.Width / 2.0,
                                 BoundsDip.Top + BoundsDip.Height / 2.0);
            }
        }
    }
}