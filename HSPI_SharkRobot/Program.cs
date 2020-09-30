using System;
using System.Runtime.CompilerServices;

namespace HSPI_SharkRobot
{
	public class Program {
		public static void Main(string[] args) {
			HSPI plugin = new HSPI();
			plugin.Connect(args);
		}
	}
}