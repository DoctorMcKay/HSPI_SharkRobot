namespace HSPI_SharkRobot.Enums {
	public enum HsStatus {
		Disconnected = 0,
		Charging = 1,
		FullyChargedOnDock = 2,
		ChargingToResume = 3,
		Evacuating = 4,
		
		NotRunning = 10,
		Running = 11,
		SpotClean = 12,
		ReturnToDock = 13,
		
		Stuck = 20,
		
		UnknownError = 99
	}
}
