using System;
using System.Collections.Generic;
using System.IO;

namespace HSPI_SharkRobot.DataContainers {
	public class AreasToClean {
		private string _listId;
		private readonly List<string> _rooms;

		public AreasToClean(string listId) {
			_listId = listId;
			_rooms = new List<string>();
		}

		public void AddRoom(string room) {
			_rooms.Add(room);
		}

		public string Serialize() {
			MemoryStream stream = new MemoryStream();
			BinaryWriter writer = new BinaryWriter(stream);

			byte totalRoomLength = 0;
			foreach (string room in _rooms) {
				// 2 bytes per room; 1 for 0x01 prefix, 1 for byte-length prefix
				totalRoomLength += (byte) (2 + room.Length);
			}

			writer.Write((byte) 0x80);
			writer.Write((byte) 0x01);
			writer.Write((byte) 0x0b);
			writer.Write((byte) 0xca);
			writer.Write((byte) 0x02);
			writer.Write((byte) (totalRoomLength + 2 + _listId.Length));

			foreach (string room in _rooms) {
				writer.Write((byte) 0x0a);
				writer.Write(room); // includes length prefix
			}

			writer.Write((byte) 0x1a);
			writer.Write(_listId);

			byte[] bytes = stream.ToArray();
			writer.Dispose();
			stream.Dispose();

			return Convert.ToBase64String(bytes);
		}
	}
}
