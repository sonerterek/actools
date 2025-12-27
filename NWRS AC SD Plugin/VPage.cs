using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace NWRS_AC_SDPlugin
{
	public static class VPages
	{
		private static readonly Dictionary<string, IVPage> _vPages = [];
		private static readonly Stack<IVPage> _vPageStack = new();

		internal static void Add(VPage vPage)
		{
			// Allow re-adding pages (CM will re-send definitions on reconnect)
			if (_vPages.ContainsKey(vPage.Name))
			{
				Debug.WriteLine($"?? VPages: Page '{vPage.Name}' already exists - replacing with new definition");
				// Dispose the old page if it's disposable
				if (_vPages[vPage.Name] is IDisposable disposable)
				{
					try { disposable.Dispose(); } catch { }
				}
			}
			_vPages[vPage.Name] = vPage;
		}

		internal static void Remove(VPage vPage)
		{
			// More forgiving - only remove if it exists
			if (_vPages.ContainsKey(vPage.Name))
			{
				_vPages.Remove(vPage.Name);
			}
			else
			{
				Debug.WriteLine($"?? VPages: Attempted to remove non-existent page '{vPage.Name}'");
			}
		}

		internal static void Activate(string name)
		{
			if (Push(name)) {
				_vPages[name].Activate();
			}
		}

		internal static bool Push(string name)
		{
			if (!_vPages.ContainsKey(name))
			{
				Debug.WriteLine($"? VPages: Cannot push non-existent page '{name}'");
				return false;
			}
			
			IVPage curVPage = _vPageStack.Count > 0 ? _vPageStack.Peek() : null;
			if (curVPage?.Name != name) {
				_vPageStack.Push(_vPages[name]);
				return true;
			}
			return false;
		}

		internal static void Return(string name)
		{
			if (name != null && !_vPages.ContainsKey(name))
			{
				Debug.WriteLine($"? VPages: Cannot return to non-existent page '{name}'");
				return;
			}
			
			if (_vPageStack.Count == 0)
			{
				Debug.WriteLine($"? VPages: Cannot return - page stack is empty");
				return;
			}
			
			var activeVPage = _vPageStack.Pop();
			activeVPage?.Deactivate();
			
			if (name != null && activeVPage?.Name != name)
			{
				Debug.WriteLine($"?? VPages: Expected to return from page '{name}' but was '{activeVPage?.Name}'");
			}
			
			if (_vPageStack.Count == 0)
			{
				Debug.WriteLine($"?? VPages: Page stack is now empty after return");
				return;
			}
			
			var nextPage = _vPageStack.Peek();
			nextPage?.Activate();
		}

		/// <summary>
		/// Get a VPage by name
		/// </summary>
		public static VPage GetByName(string name)
		{
			return _vPages.TryGetValue(name, out var vPage) ? vPage as VPage : null;
		}

		/// <summary>
		/// Get all pages - used when updating key definitions
		/// </summary>
		public static List<VPage> GetAllPages()
		{
			return _vPages.Values.OfType<VPage>().ToList();
		}

		/// <summary>
		/// Clear all pages - used when client disconnects
		/// </summary>
		public static void ClearAll()
		{
			Debug.WriteLine($"?? VPages: Clearing all pages ({_vPages.Count} pages)");
			
			// Don't call Dispose() on pages - it tries to Remove from the dictionary
			// we're about to clear anyway, which causes iteration exceptions
			_vPages.Clear();
			_vPageStack.Clear();
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
			// Safe disposal - VPages.Remove is now forgiving
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
