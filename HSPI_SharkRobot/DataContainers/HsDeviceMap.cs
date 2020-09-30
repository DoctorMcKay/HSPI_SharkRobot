namespace HSPI_SharkRobot.DataContainers {
	public struct HsDeviceMap {
		public Device SharkDevice;
		public DeviceProperties? LastProperties;
		public int HsDeviceRef;
		public int HsFeatureRefStatus;
		public int HsFeatureRefPowerMode;
		public int HsFeatureRefBattery;
	}
}