using System;
using System.Drawing;
using System.Threading.Tasks;

namespace NWRS_AC_SDPlugin
{
	public interface IVPage
	{
		string Name { get; }
		IVPage AddVKey(string name, (int row, int col) vKeyPos, string targetVPage);

		void SetVKey(int row, int col, VKey vKey);
		void RemoveVKey(int row, int col, VKey vKey);
		void Activate();
		void Deactivate();
	}
}
