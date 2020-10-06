// ReSharper disable InconsistentNaming
namespace HSPI_SharkRobot.DataContainers {
	public struct RoomDefinitions {
		public GoZone[] goZones;

		public struct GoZone {
			public string name;
			public double[][] points;
		}
	}
}
