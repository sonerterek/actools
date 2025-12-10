using System.Windows;
using System.Windows.Media;

namespace AcManager.UiObserver
{
	/// <summary>
	/// Interface for debug rectangle visualization.
	/// Allows flexible styling and positioning of debug overlays.
	/// </summary>
	internal interface IDebugRect
	{
		/// <summary>
		/// Gets the rectangle bounds in DIP coordinates.
		/// </summary>
		Rect Bounds { get; }
		
		/// <summary>
		/// Gets the center point in DIP coordinates (for navigation calculations).
		/// Used to draw a small dot showing where navigation distances are measured from.
		/// </summary>
		Point? CenterPoint { get; }
		
		/// <summary>
		/// Gets the stroke color for the rectangle border.
		/// </summary>
		Brush StrokeBrush { get; }
		
		/// <summary>
		/// Gets the stroke thickness in DIP units.
		/// </summary>
		double StrokeThickness { get; }
		
		/// <summary>
		/// Gets the fill brush (usually transparent).
		/// </summary>
		Brush FillBrush { get; }
		
		/// <summary>
		/// Gets optional inset (in DIP) to apply to bounds.
		/// Useful for making nested elements visible inside parent boundaries.
		/// </summary>
		double Inset { get; }
	}
}
