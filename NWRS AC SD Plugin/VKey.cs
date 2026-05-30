using System;

namespace NWRS_AC_SDPlugin
{
	public class VKey
	{
		private readonly string _name;
		private readonly VPage _vPage;
		private readonly int _row;
		private readonly int _col;
		private string _image;
		private string _imageInv;
		private string _title;
		public bool State { get; private set; }
		public bool Enabled { get; private set; }

		private readonly Action<VKey> _onPress;
		private readonly int _minPress;

		// Event to notify external systems (like CM) about key presses
		public static event Action<string> OnKeyPressedExternal;

		private long _msDown = 0;

		// public static VKey DebugKey = new VKey("Debug", () => MainWindow.CurrentApp.DBGDisplayTree());

		public VKey(string name, VPage vPage, int row, int col, Action<VKey> onPress, int minPress = 0, string image = null, string title = null, bool state = false, bool enabled = true)
		{
			_name = name;
			_vPage = vPage;
			_row = row;
			_col = col;

			Enabled = enabled;			
			State = state;
			SetImage(image);
			_title = title;
			_onPress = onPress;
			_minPress = minPress;
			vPage.SetVKey(row, col, this);
		}

		public VKey(string name, VPage vPage, int row, int col, Action onPress = null, int minPress = 0, string image = null, string title = null, bool state = false)
			: this(name, vPage, row, col, vkey => onPress.Invoke(), minPress, image, title, state)
		{
		}

		public VKey(string name, VPage vPage, int row, int col, string targetVPage, int minPress = 0, string image = null, string title = null, bool state = false)
			: this(name, vPage, row, col, () => VPages.Activate(targetVPage), minPress, image, title, state)
		{
		}

		public VKey(VPage vPage, int row, int col, string targetVPage)
			: this("BLANK", vPage, row, col, targetVPage, 1, SDImage.BlankImage(), title: null, state: false)
		{
		}

		/*
		public void Attach(SDKey sdKey)
		{
			_sdKey = sdKey;
			_sdKey.SetVKey(this);
		}

		public void Detach()
		{
			_sdKey.SetVKey(null);
			_sdKey = null;
		}
		*/

		public void SetImage(string image)
		{
			var sdImage = image is not null ? new SDImage(image ?? _name, false) : null;
			_image = sdImage?.GetImage();
			sdImage = image is not null ? new SDImage(image ?? _name, true) : null;
			_imageInv = sdImage?.GetImage();
			_vPage.NewKeyImage(_row, _col);
		}

		public void SetState(bool state)
		{
			State = state;
			_vPage.NewKeyImage(_row, _col);
		}

		public void SetTitle(string title)
		{
			_title = title;
			_vPage.NewKeyTitle(_row, _col);
		}

		public void Disable()
		{
			Enabled = false;
			_vPage.NewKeyImage(_row, _col);
		}

		public void Enable()
		{
			Enabled = true;
			_vPage.NewKeyImage(_row, _col);
		}

		public string GetImage()
		{
			return Enabled ? (State ? _imageInv : _image) : SDImage.BlankImage();
		}

		public string GetTitle()
		{
			return _title;
		}

		public void OnKeyPress(int msDownDuration)
		{
			if (_onPress != null && msDownDuration >= _minPress) {
				_onPress.Invoke(this);
				
				// Notify external systems (like Content Manager) about the key press
				OnKeyPressedExternal?.Invoke(_name);
			}
		}
	}
}
