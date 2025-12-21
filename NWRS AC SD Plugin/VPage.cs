using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace NWRS_AC_SDPlugin
{
	public static class VPages
	{
		private static readonly Dictionary<string, IVPage> _vPages = [];
		private static readonly Stack<IVPage> _vPageStack = new();

		internal static void Add(VPage vPage)
		{
			Debug.Assert(!_vPages.ContainsKey(vPage.Name));
			_vPages.Add(vPage.Name, vPage);
		}

		internal static void Remove(VPage vPage)
		{
			Debug.Assert(_vPages.ContainsKey(vPage.Name));
			_vPages.Remove(vPage.Name);
		}

		internal static void Activate(string name)
		{
			if (Push(name)) {
				_vPages[name].Activate();
			}
		}

		internal static bool Push(string name)
		{
			Debug.Assert(_vPages.ContainsKey(name));
			IVPage curVPage = _vPageStack.Count > 0 ? _vPageStack.Peek() : null;
			if (curVPage?.Name != name) {
				_vPageStack.Push(_vPages[name]);
				return true;
			}
			return false;
		}

		internal static void Return(string name)
		{
			Debug.Assert(name is null || _vPages.ContainsKey(name));
			var activeVPage = _vPageStack.Pop();
			activeVPage?.Deactivate();
			Debug.Assert(name is null || activeVPage.Name == name);
			var nextPage = _vPageStack.Peek();
			nextPage.Activate();
		}
	}

	public class VPage : IVPage, IDisposable  // Restore IDisposable interface
	{
		public string Name { get; }
		public int Rows { get; }
		public int Cols { get; }
		public VKey[,] VKeys { get; }
		public bool IsActive { get; private set; } = false;
		private int _scrollOffset = 0;		// Used for scrolling
		private readonly int _firstScrollRow = 0;
		// private readonly bool _partialVPage = false; // Number of rows to show when scrolling

		public VPage(string name, int rows = 5, int cols = 3, int firstScrollRow = 0) //, bool partialVPage = false)
		{
			Name = name;
			Rows = rows;
			Cols = cols;
			_firstScrollRow = firstScrollRow;
			// _partialVPage = partialVPage;
			VKeys = new VKey[rows, cols];
			VPages.Add(this);
		}

		public VPage()
			: this("BLANK")
		{
			for (var r = 0; r < Rows; r++) {
				for (var c = 0; c < Cols; c++) {
					VKeys[r, c] = new VKey(this, r, c, "Main Menu");
				}
			}
		}

		public void Dispose()
		{
			VPages.Remove(this);
		}

		public VPage AddVKey(int row, int col, string name, Action onPress = null, int minPress = 0, string image = null, string title = null)
		{
			new VKey(name, this, row, col, onPress, minPress, image, title);
			return this;
		}

		public IVPage AddVKey(string name, (int row, int col) vKeyPos, string targetName)
		{
			new VKey(name, this, vKeyPos.row, vKeyPos.col, vkey => VPages.Activate(name));
			return this;
		}

		public void SetVKey(int row, int col, VKey vKey)
		{
			VKeys[row, col] = vKey;
			SDeck.SDPage.NewVKey(this, row, col);
		}

		public void RemoveVKey(int row, int col, VKey vKey)
		{
			if (VKeys[row, col] == vKey) {
				VKeys[row, col] = null;
				SDeck.SDPage.NewVKey(this, row, col);
			}
		}

		public void Clear()
		{
			for (int row = 0; row < Rows; row++) {
				for (int col = 0; col < Cols; col++) {
					VKeys[row, col] = null;
				}
			}
		}

		public void NewKeyImage(int row, int col)
		{
			SDeck.SDPage.NewVKey(this, row, col);
		}

		public void NewKeyTitle(int row, int col)
		{
			SDeck.SDPage.NewVKey(this, row, col);
		}

		// Typically not called directly. Calling directly bypasses VPages
		public void Activate()
		{
			Debug.WriteLine($"Activating VPage {Name}");
			IsActive = true;
			SDeck.SetVPage(this);
			Refresh();
		}

		// Typically not called directly. Calling directly bypasses VPages
		public void Deactivate()
		{
			Debug.WriteLine($"Deactivating VPage {Name}");
			IsActive = false;
		}

		private void Refresh()
		{
			for (int row = 0; row < SDeck.SDPage.Rows; row++) {
				for (int col = 0; col < SDeck.SDPage.Cols; col++) {
					SDeck.SDPage.NewVKey(this, row, col);
				}
			}
		}

		public void Scroll(int rows)
		{
			_scrollOffset += rows;
			if (_scrollOffset < 0) {
				_scrollOffset = 0;
			}
			if (_scrollOffset >= Rows) {
				_scrollOffset = Rows - 1;
			}
			// BUG BUG: Improve performance by only refreshing changed rows
			Refresh();
		}
	}
}
