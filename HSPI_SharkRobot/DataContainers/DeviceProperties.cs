﻿using HSPI_SharkRobot.Enums;

namespace HSPI_SharkRobot.DataContainers {
	public struct DeviceProperties {
		public string DeviceName;
		public int BatteryCapacity;
		public SharkOperatingMode OperatingMode;
		public SharkPowerMode PowerMode;
		public bool ChargingStatus;
		public bool DockedStatus;
		public int? ErrorCode;
		public bool RechargingToResume;
		public RoomList? RoomList;
	}
}