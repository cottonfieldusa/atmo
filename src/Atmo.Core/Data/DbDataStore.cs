﻿// ================================================================================
//
// Atmo 2
// Copyright (C) 2011  BARANI DESIGN
//
// This program is free software; you can redistribute it and/or
// modify it under the terms of the GNU General Public License
// as published by the Free Software Foundation; either version 2
// of the License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
// 
// Contact: Jan Barani mailto:jan@baranidesign.com
//
// ================================================================================

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using Atmo.Stats;
using Atmo.Units;
using System.Threading;

namespace Atmo.Data {

	
	public class DbDataStore : IDataStore, IDisposable {

		private class DbSensorInfo : ISensorInfo {

			private readonly string _lastHardwareId;

			public DbSensorInfo(string name, string lastHardwareId) {
				Name = name;
				_lastHardwareId = lastHardwareId;
			}

			public string Name { get; private set; }

			public SpeedUnit SpeedUnit {
				get { return SpeedUnit.MetersPerSec; }
			}

			public TemperatureUnit TemperatureUnit {
				get { return TemperatureUnit.Celsius; }
			}

			public PressureUnit PressureUnit {
				get { return PressureUnit.Pascals; }
			}

			public override string ToString() {
				return Name + ' ' + _lastHardwareId;
			}

		}


		private static readonly TimeSpan OneMinute = new TimeSpan(0,1,0);
		private static readonly TimeSpan TenMinutes = new TimeSpan(0,10,0);
		private static readonly TimeSpan OneHour = new TimeSpan(1,0,0);
		private static readonly TimeSpan OneDay = new TimeSpan(1,0,0,0);
		private const int RecordBatchQuantity = UInt16.MaxValue;

		/// <summary>
		/// THIS LIST MUST BE SORTED, SMALLEST TO LARGEST
		/// </summary>
		private static readonly TimeSpan[] ValidSummaryTimeSpans = new[] {
			OneMinute, 
			TenMinutes
		};

		static DbDataStore() {
			Array.Sort(ValidSummaryTimeSpans);
		}

		private readonly IDbConnection _connection;

		public DbDataStore(IDbConnection connection) {
			if (null == connection)
				throw new ArgumentNullException("connection");
			
			_connection = connection;
		}

		~DbDataStore() {
			Dispose(false);
		}

		#region Record Modification Methods

		public bool AdjustTimeStamps(
			string sensorName,
			TimeRange currentRange,
			TimeRange correctedRange,
			bool canOverwrite
		) {
			return AdjustTimeStamps(
				sensorName,
				new PosixTimeRange(currentRange),
				new PosixTimeRange(correctedRange),
				canOverwrite
			);
		}

		public bool AdjustTimeStamps(
			string sensorName,
			PosixTimeRange currentRange,
			PosixTimeRange correctedRange,
			bool canOverwrite
		) {
			int delta = correctedRange.Span - currentRange.Span;
			int offset = correctedRange.Low - currentRange.Low;

			if (delta == 0 && offset == 0)
				return false;

			if (!ForceConnectionOpen())
				throw new Exception("Could not open database.");

			int sensorId = -1;

			using(var command = _connection.CreateTextCommand(
				"SELECT sensorID FROM Sensor WHERE nameKey = @nameKey")) {
				command.AddParameter("nameKey", DbType.String, sensorName);
				using (var reader = command.ExecuteReader()) {
					if (reader.Read()) {
						sensorId = reader.GetInt32(0);
					}
				}
			}

			if (sensorId < 0)
				throw new KeyNotFoundException("Sensor not found in datastore.");

			if (
				!canOverwrite
				&& (correctedRange.Low < currentRange.Low || correctedRange.High > currentRange.High)
			) {
				using (var command = _connection.CreateTextCommand("SELECT COUNT(*) FROM Record WHERE sensorId = @sensorId")) {

					//var nameIdParam = command.AddParameter("sensorId", DbType.Int32, sensorId);

					var orJoin = new List<string>();
					if (correctedRange.Low < currentRange.Low) {
						orJoin.Add("(stamp >= @corLow AND stamp < @curLow)");
					}

					//var corLowParam = command.AddParameter("corLow", DbType.Int32, correctedRange.Low);
					//var curLowParam = command.AddParameter("curLow", DbType.Int32, currentRange.Low);

					if (correctedRange.High > currentRange.High) {
						orJoin.Add("(stamp <= @corHigh AND stamp > @curHigh)");
					}
					//var corHighParam = command.AddParameter("corHigh", DbType.Int32, correctedRange.High);
					//var curHighParam = command.AddParameter("curHigh", DbType.Int32, currentRange.High);

					command.CommandText += " AND (" +
						String.Join(" OR ", orJoin.ToArray()) +
						") AND NOT (stamp >= @curLow AND stamp <= @curHigh)";

					int count = 0;
					using (var reader = command.ExecuteReader()) {
						if (reader.Read())
							count = reader.GetInt32(0);

					}
					if (count > 0) {
						throw new InvalidOperationException("Cannot overwrite existing datastore entries.");
					}
				}
			}

			// at this point there is space to shift or grow into
			bool ok = false;
			if (delta < 0) {
				// shrinking
				ok = ShrinkRecords(sensorId, currentRange, delta);

				// then shift all if needed
				if (offset != 0) {
					ok = (0 != OffsetRecords(sensorId, new PosixTimeRange(currentRange.Low, currentRange.Low + correctedRange.Span), offset))
						| ok;
				}
			}
			else {
				// need to shift the start into place
				if (offset != 0) {
					ok = 0 != OffsetRecords(sensorId, currentRange, offset);
				}
				// now we need to stretch the records
				if (delta > 0) {
					//ok |= InterpolateInsertRecords(sensorId, new PosixTimeRange(currentRange.High - offset + 1, correctedRange.High), correctedRange);
					ok |= ExpandRecords(sensorId, new PosixTimeRange(currentRange.Low + offset, currentRange.High + offset), delta);
				}
			}

			

			bool okUpdateCurrent, okUpdateCorrected;

			if(currentRange.Intersects(correctedRange)) {
				var completeRange = new PosixTimeRange(
					new PosixTime(Math.Min(currentRange.Low, correctedRange.Low)),
					new PosixTime(Math.Max(currentRange.High, correctedRange.High))
				);
				okUpdateCurrent = okUpdateCorrected =
					UpdateSummaryRecords(completeRange, sensorName, sensorId);
			}
			else {
				okUpdateCurrent = UpdateSummaryRecords(currentRange, sensorName, sensorId);
				okUpdateCorrected = UpdateSummaryRecords(correctedRange, sensorName, sensorId);
			}

			return ok && okUpdateCurrent && okUpdateCorrected;
		}

		private int OffsetRecords(int sensorId, PosixTimeRange range, int offset) {
			int c = 0;
			if (0 == offset)
				return 0;
			
			if (!ForceConnectionOpen())
				throw new Exception("Could not open database.");

			const string delLowCommandText = "DELETE FROM Record WHERE stamp >= @minDelRangeInclusive AND stamp < @maxDelRangeExclusive";
			const string delHighCommandText = "DELETE FROM Record WHERE stamp > @minDelRangeExclusive AND stamp <= @maxDelRangeInclusive";
			const string shiftCommandText = "UPDATE Record SET stamp = stamp + @offset WHERE sensorId = @sensorId AND stamp = @stamp";

			using (var command = _connection.CreateTextCommand(String.Empty)) {
				command.Transaction = _connection.BeginTransaction();
				if (offset < 0) {
					command.CommandText = delLowCommandText;
					var minDelRangeInclusive = command.AddParameter("minDelRangeInclusive", DbType.Int32, range.Low.Value + offset);
					var maxDelRangeExclusive = command.AddParameter("maxDelRangeExclusive", DbType.Int32, range.Low);
					var deletedCount = command.ExecuteNonQuery();

					command.CommandText = shiftCommandText;
					var sensorIdParam = command.AddParameter("sensorId", DbType.Int32, sensorId);
					var stampParam = command.AddParameter("stamp", DbType.Int32, 0);
					var offsetParam = command.AddParameter("offset", DbType.Int32, offset);
					for (int i = range.Low; i <= range.High; i++) {
						stampParam.Value = i;
						c += command.ExecuteNonQuery();
					}
				}
				else {
					command.CommandText = delHighCommandText;
					var minDelRangeExclusive = command.AddParameter("minDelRangeExclusive", DbType.Int32, range.High);
					var maxDelRangeInclusive = command.AddParameter("maxDelRangeInclusive", DbType.Int32, range.High.Value + offset);
					var deletedCount = command.ExecuteNonQuery();

					command.CommandText = shiftCommandText;
					var sensorIdParam = command.AddParameter("sensorId", DbType.Int32, sensorId);
					var stampParam = command.AddParameter("stamp", DbType.Int32, 0);
					var offsetParam = command.AddParameter("offset", DbType.Int32, offset);
					for (int i = range.High; i >= range.Low; i--) {
						stampParam.Value = i;
						c += command.ExecuteNonQuery();
					}
				}
				command.Transaction.Commit();
			}
			return c;
		}

		private bool ExpandRecords(int sensorId, PosixTimeRange range, int delta) {

			if (delta < 0) {
				return false;
			}
			if (delta == 0) {
				return true;
			}

			int totalInserts = delta;
			int rangeSpan = range.Span;
			int resultingSpan = rangeSpan + delta;
			if (totalInserts >= resultingSpan) {
				return false;
			}

			int splitParts = Math.Min(rangeSpan, delta + 1);


			const string moveCommandText =
				"UPDATE OR REPLACE Record SET stamp = (stamp + @offset) WHERE sensorId = @sensorId AND stamp = @stamp";
			const string delOverflowCommand =
				"DELETE FROM Record WHERE stamp > @minRange AND stamp < @maxRange";

			using (var command = _connection.CreateTextCommand(String.Empty)) {
				command.Transaction = _connection.BeginTransaction();

				command.CommandText = delOverflowCommand;
				command.AddParameter("minRange", DbType.Int32, range.High);
				command.AddParameter("maxRange", DbType.Int32, range.High.Value + delta);
				command.ExecuteNonQuery();
				
				var sensorIdParam = command.AddParameter("sensorId", DbType.Int32, sensorId);
				var offsetParam = command.AddParameter("offset", DbType.Int32, 0);
				var stampParam = command.AddParameter("stamp", DbType.Int32, 0);

				command.CommandText = moveCommandText;
				for (int partIndex = splitParts - 1; partIndex >= 0; partIndex--) {

					int curPartIndex = ((partIndex * rangeSpan) / splitParts);
					int nextPartIndex = Math.Min(((partIndex + 1) * rangeSpan) / splitParts, rangeSpan);
					int destIndex = (curPartIndex * (resultingSpan - 1)) / (rangeSpan - 1);
					int shift = (destIndex + range.Low) - (curPartIndex + range.Low);

					// move all between ["curPartIndex","nextPartIndex") up by "shift"
					//minStampParam.Value = curPartIndex;
					//maxStampParam.Value = nextPartIndex;
					offsetParam.Value = shift;
					for (int i = nextPartIndex + range.Low - 1; i >= curPartIndex + range.Low; i--) {
						stampParam.Value = i;
						command.ExecuteNonQuery();
					}

				}
				command.Transaction.Commit();
			}
			return true;

		}

		private bool ShrinkRecords(int sensorId, PosixTimeRange range, int delta) {
			if (delta > 0) {
				return false;
			}
			if (0 == delta) {
				return true;
			}

			int totalDeletions = -delta;
			int rangeSpan = range.Span;
			if (rangeSpan < totalDeletions) {
				return false;
			}

			using (var command = _connection.CreateTextCommand(
				"UPDATE OR REPLACE Record SET stamp = stamp - @offset WHERE sensorId = @sensorId AND stamp = @stamp")) {
				command.Transaction = _connection.BeginTransaction();

				var sensorIdParam = command.AddParameter("sensorId", DbType.Int32, sensorId);
				var stampParam = command.AddParameter("stamp", DbType.Int32, 0);
				var offsetParam = command.AddParameter("offset", DbType.Int32, 0);
				
				for (int delNumber = 0; delNumber < totalDeletions; delNumber++) {
					// scoot it down
					int delIndex = (rangeSpan * (delNumber + 1)) / ((rangeSpan - totalDeletions) + 1);
					int nextIndex = (rangeSpan * (delNumber + 2)) / ((rangeSpan - totalDeletions) + 1);
					nextIndex = Math.Min(nextIndex, range.High);
					offsetParam.Value = delNumber + 1;
					for (int i = delIndex; i < nextIndex; i++) {
						stampParam.Value = i;
						command.ExecuteNonQuery();
					}
				}

				command.CommandText = "DELETE FROM Record WHERE sensorId = @sensorId AND stamp > @minStamp AND stamp <= @maxStamp";
				command.Parameters.Clear();
				command.Parameters.Add(sensorIdParam);
				var minStampParam = command.AddParameter("minStamp", DbType.Int32, range.High - totalDeletions);
				var maxStampParam = command.AddParameter("maxStamp", DbType.Int32, range.High);

				command.ExecuteNonQuery();
				command.Transaction.Commit();
			}

			return true;
		}

		#endregion

		#region Basic CRUD operations

		public DateTime GetMaxSyncStamp() {
			if (!ForceConnectionOpen())
				throw new Exception("Could not open database.");

			DateTime minStamp = default(DateTime);
			using (var command = _connection.CreateTextCommand("SELECT stamp FROM syncstamps ORDER BY stamp DESC LIMIT 1")) {
				using (var reader = command.ExecuteReader()) {
					if (reader.Read()) {
						minStamp = UnitUtility.ConvertFromPosixTime(reader.GetInt32(0));
					}
				}
			}
			if (default(DateTime).Equals(minStamp)) {
				using (var command = _connection.CreateTextCommand("SELECT stamp FROM dayrecord ORDER BY stamp DESC LIMIT 1")) {
					// todo: pull this from the 10minute instead?
					using (var reader = command.ExecuteReader()) {
						if (reader.Read()) {
							minStamp = UnitUtility.ConvertFromPosixTime(reader.GetInt32(0));
						}
					}
				}
			}
			return minStamp;
		}

		public bool PushSyncStamp(DateTime stamp) {
			return PushSyncStamp(UnitUtility.ConvertToPosixTime(stamp));
		}

		/// <param name="stamp">A posix time stamp.</param>
		private bool PushSyncStamp(int stamp) {
			using (IDbCommand command = _connection.CreateTextCommand("INSERT INTO syncstamps (stamp) VALUES (@stamp)")) {
				command.AddParameter("stamp", DbType.Int32, stamp);
				return 1 == command.ExecuteNonQuery();
			}
		}

		IEnumerable<IReading> IDataStore.GetReadings(string sensor, DateTime from, TimeSpan span) {
			return GetReadings(sensor, from, span).OfType<IReading>();
		}

		public string GetLatestSensorNameForHardwareId(string hardwareId) {
			if (!ForceConnectionOpen()) {
				throw new Exception("Could not open database.");
			}
			using (var command = _connection.CreateTextCommand(
				"SELECT nameKey FROM Sensor"
				+ " LEFT JOIN dayrecord on dayrecord.sensorId = Sensor.sensorId"
				+ " WHERE Sensor.lastLoadHardwareId = @hardwareId"
				+ " GROUP BY dayrecord.sensorId"
				+ " ORDER BY MAX(dayrecord.stamp),Sensor.SensorID DESC"
			)) {
				command.AddParameter("hardwareId", DbType.String, hardwareId);
				using (var reader = command.ExecuteReader()) {
					if (reader.Read()) {
						return reader.GetValue(0) as string;
					}
				}
			}
			return null;
		}

		public void SetLatestSensorNameForHardwareId(string dbSensorName, string hardwareId) {
			if (!ForceConnectionOpen())
				throw new Exception("Could not open database.");

			using (var command = _connection.CreateTextCommand("UPDATE Sensor SET lastLoadHardwareId = @hardwareId WHERE nameKey = @nameKey")) {
				command.AddParameter("hardwareId", DbType.String, hardwareId);
				command.AddParameter("nameKey", DbType.String, dbSensorName);
				command.ExecuteNonQuery();
			}
		}

		public IEnumerable<ISensorInfo> GetAllSensorInfos() {
			if (!ForceConnectionOpen())
				throw new Exception("Could not open database.");
			
			var sensorInfos = new List<ISensorInfo>();
			using (var command = _connection.CreateTextCommand("SELECT nameKey,lastLoadHardwareId FROM sensor")) {
				using (var reader = command.ExecuteReader()) {
					while (reader.Read()) {
						sensorInfos.Add(new DbSensorInfo(
							reader.GetValue(0) as string,
							reader.GetValue(1) as string
						));
					}
				}
			}
			return sensorInfos;
		}

		public IEnumerable<PackedReading> GetReadings(string sensor, DateTime from, TimeSpan span) {
			DateTime to = from.Add(span);
			if (to < from) {
				DateTime s = to;
				to = from;
				from = s;
			}
			if (!ForceConnectionOpen()) {
				throw new Exception("Could not open database.");
			}
			using (var command = _connection.CreateTextCommand(
				"SELECT stamp,[values] FROM Record"
				+ " INNER JOIN Sensor ON (Sensor.sensorId = Record.sensorId)"
				+ " WHERE Sensor.nameKey = @sensorNameKey"
				+ " AND Record.stamp >= @minPosixStamp"
				+ " AND Record.stamp <= @maxPosixStamp"
				+ " ORDER BY stamp " + ((span < TimeSpan.Zero) ? "DESC" : "ASC")
			)) {
				command.AddParameter("minPosixStamp", DbType.Int32, UnitUtility.ConvertToPosixTime(from));
				command.AddParameter("maxPosixStamp", DbType.Int32, UnitUtility.ConvertToPosixTime(to));
				command.AddParameter("sensorNameKey", DbType.String, sensor);

				using (var reader = command.ExecuteReader()) {
					int ordStamp = reader.GetOrdinal("stamp");
					int ordValues = reader.GetOrdinal("values");
					byte[] values = new byte[8];
					const int chunkSize = 256;
					bool isReading = true;
					var cache = new List<PackedReading>(chunkSize);

					while (isReading) {
						cache.Clear();
						for (int i = 0; i < chunkSize; i++) {
							if(!reader.Read()) {
								isReading = false;
								break;
							}
							reader.GetBytes(ordValues, 0, values, 0, values.Length);
							cache.Add(new PackedReading(
								UnitUtility.ConvertFromPosixTime(reader.GetInt32(ordStamp)),
								PackedReadingValues.ConvertFromPackedBytes(values, 0)
							));
						}
						foreach(var item in cache) {
							yield return item;
						}
					}
				}
			}
		}

		public bool AddSensor(ISensorInfo sensor) {
			if (null == sensor) {
				throw new ArgumentNullException("sensor");
			}
			if (!ForceConnectionOpen()) {
				throw new Exception("Could not open database.");
			}
			int? sensorRecordId = null;
			using (var getSensorCmd = _connection.CreateTextCommand("SELECT sensorId FROM Sensor WHERE nameKey = '" + sensor.Name + "'")) {
				using (var getSensorReader = getSensorCmd.ExecuteReader()) {
					if (getSensorReader.Read()) {
						sensorRecordId = getSensorReader.GetInt32(getSensorReader.GetOrdinal("sensorId"));
					}
				}
			}
			if (!sensorRecordId.HasValue) {
				using (var addSensorCmd = _connection.CreateTextCommand(
					"INSERT INTO Sensor(nameKey,lastLoadHardwareId)"
					+ " VALUES (@nameKey,@lastHardwareId)"
				)) {
					addSensorCmd.AddParameter("nameKey", DbType.String, sensor.Name);
					addSensorCmd.AddParameter("lastHardwareId", DbType.String, String.Empty);
					addSensorCmd.ExecuteNonQuery();
				}
				using (var getSensorCmd = _connection.CreateTextCommand("SELECT sensorId FROM Sensor WHERE nameKey = '" + sensor.Name + "'")) {
					using (var getSensorReader = getSensorCmd.ExecuteReader()) {
						if (getSensorReader.Read()) {
							sensorRecordId = getSensorReader.GetInt32(getSensorReader.GetOrdinal("sensorId"));
							return true;
						}
					}
				}
			}
			return false;
		}

		bool IDataStore.Push(string sensor, IEnumerable<IReading> readings) {
			return Push(sensor, readings);
		}

		public bool Push<T>(string sensor, IEnumerable<T> readings) where T : IReading {
			return Push(sensor, readings, false);
		}

		private TimeRange PushReadings<T>(
			IDbCommand command, IEnumerable<T> readings, Func<T,byte[]> conversion,
			DbParameter stampParam, DbParameter valuesParam
		)
			where T:IReading
		{
			DateTime minStamp = new DateTime(9999, 1, 1);
			DateTime maxStamp = new DateTime(0);
			int counter = 0;

			try {
				command.Transaction = _connection.BeginTransaction();
				foreach (var reading in readings) {
					stampParam.Value = UnitUtility.ConvertToPosixTime(reading.TimeStamp);
					valuesParam.Value = conversion(reading);
					command.ExecuteNonQuery();
					counter++;
					if (counter >= RecordBatchQuantity) {
						counter = 0;
						command.Transaction.Commit();
						command.Transaction = _connection.BeginTransaction();
					}
					if (reading.TimeStamp < minStamp) {
						minStamp = reading.TimeStamp;
					}
					if (reading.TimeStamp > maxStamp) {
						maxStamp = reading.TimeStamp;
					}
				}
				if (command.Transaction != null) {
					command.Transaction.Commit();
				}
			}
			catch(Exception ex) {
				if(null != command.Transaction) {
					command.Transaction.Rollback();
				}
			}

			return new TimeRange(minStamp,maxStamp);
		}

		public bool Push<T>(string sensor, IEnumerable<T> readings, bool replace) where T : IReading {
			if (String.IsNullOrEmpty(sensor))
				throw new ArgumentOutOfRangeException("sensor");
			
			if (!ForceConnectionOpen())
				throw new Exception("Could not open database.");
			
			int? sensorRecordId = null;
			using (var getSensorCmd = _connection.CreateTextCommand("SELECT sensorId FROM Sensor WHERE nameKey = '" + sensor + "'")) {
				using (var getSensorReader = getSensorCmd.ExecuteReader()) {
					if (getSensorReader.Read()) {
						sensorRecordId = getSensorReader.GetInt32(getSensorReader.GetOrdinal("sensorId"));
					}
				}
			}

			if (!sensorRecordId.HasValue)
				return false;
			
			TimeRange insertTimeRange = default(TimeRange);
			
			// insert the initial values
			using (var pushRecordCommand = _connection.CreateTextCommand(
				"INSERT OR " + (replace ? "REPLACE" : "IGNORE") +
				" INTO Record (sensorId,stamp,[values])" +
				" VALUES (@sensorId,@stamp,@values)"
			)) {
				pushRecordCommand.AddParameter("sensorId", DbType.Int32, sensorRecordId.Value);
				DbParameter stampParam = pushRecordCommand.AddParameter("stamp", DbType.Int32, null);
				DbParameter valuesParam = pushRecordCommand.AddParameter("values", DbType.Binary, null);
				insertTimeRange = typeof(T) == typeof(PackedReading)
					? PushReadings(
						pushRecordCommand, readings.Cast<PackedReading>(),
						r => PackedReadingValues.ConvertToPackedBytes(r.Values),
						stampParam, valuesParam
					)
					: PushReadings(
						pushRecordCommand, readings.Cast<PackedReading>(),
						r => PackedReadingValues.ConvertToPackedBytes(r),
						stampParam, valuesParam
				);
			}

			return UpdateSummaryRecords(insertTimeRange, sensor, sensorRecordId.Value);
		}

		private bool PushSummaries(int sensorId, string tableName, IEnumerable<ReadingsSummary> summaries, PosixTimeRange totalRange) {

			string insertCommandText = String.Format(
				"INSERT INTO {0} (sensorId,stamp,minValues,maxValues,meanValues,stddevValues,recordCount,tempCount,pressCount,humCount,speedCount,dirCount)"
				+ " VALUES (@sensorId,@stamp,@minValues,@maxValues,@meanValues,@stddevValues,@recordCount,@tempCount,@pressCount,@humCount,@speedCount,@dirCount)",
				tableName
				);

			string deleteCommandText = String.Format(
				"DELETE FROM {0} WHERE stamp >= @minStamp AND stamp <= @maxStamp AND sensorId = @sensorId",
				tableName
			);

			using (var command = _connection.CreateTextCommand(deleteCommandText)) {
				command.Transaction = _connection.BeginTransaction();

				var sensorIdParam = command.AddParameter("sensorId", DbType.Int32, sensorId);
				
				if(totalRange.Span != 0) {
					command.AddParameter("minStamp", DbType.Int32, totalRange.Low);
					command.AddParameter("maxStamp", DbType.Int32, totalRange.High);
					command.ExecuteNonQuery();
				}

				command.CommandText = insertCommandText;

				var stampParam = command.AddParameter("stamp", DbType.Int32, null);
				var minValuesParam = command.AddParameter("minValues", DbType.Binary, null);
				minValuesParam.Size = 8;
				var maxValuesParam = command.AddParameter("maxValues", DbType.Binary, null);
				maxValuesParam.Size = 8;
				var meanValuesParam = command.AddParameter("meanValues", DbType.Binary, null);
				meanValuesParam.Size = 8;
				var stddevValuesParam = command.AddParameter("stddevValues", DbType.Binary, null);
				stddevValuesParam.Size = 8;
				var recordCountParam = command.AddParameter("recordCount", DbType.Int32, null);
				recordCountParam.ParameterName = "recordCount";
				var tempCountParam = command.AddParameter("tempCount", DbType.Binary, null);
				tempCountParam.Size = 0;
				var pressCountParam = command.AddParameter("pressCount", DbType.Binary, null);
				pressCountParam.Size = 0;
				var humCountParam = command.AddParameter("humCount", DbType.Binary, null);
				humCountParam.Size = 0;
				var speedCountParam = command.AddParameter("speedCount", DbType.Binary, null);
				speedCountParam.Size = 0;
				var dirCountParam = command.AddParameter("dirCount", DbType.Binary, null);
				dirCountParam.Size = 0;

				int counter = 0;
				foreach (var summary in summaries) {

					stampParam.Value = UnitUtility.ConvertToPosixTime(summary.BeginStamp);
					minValuesParam.Value = PackedReadingValues.ConvertToPackedBytes(summary.Min);
					maxValuesParam.Value = PackedReadingValues.ConvertToPackedBytes(summary.Max);
					meanValuesParam.Value = PackedReadingValues.ConvertToPackedBytes(summary.Mean);
					stddevValuesParam.Value = PackedReadingValues.ConvertToPackedBytes(summary.SampleStandardDeviation);
					recordCountParam.Value = summary.Count;

					byte[] data;
					tempCountParam.Value =
						data = PackedReadingValues.ConvertTemperatureCountsToPackedBytes(summary.TemperatureCounts);
					tempCountParam.Size = data.Length;
					pressCountParam.Value = data = PackedReadingValues.ConvertPressureCountsToPackedBytes(summary.PressureCounts);
					pressCountParam.Size = data.Length;
					humCountParam.Value = data = PackedReadingValues.ConvertHumidityCountsToPackedBytes(summary.HumidityCounts);
					humCountParam.Size = data.Length;
					speedCountParam.Value = data = PackedReadingValues.ConvertWindSpeedCountsToPackedBytes(summary.WindSpeedCounts);
					speedCountParam.Size = data.Length;
					dirCountParam.Value =
						data = PackedReadingValues.ConvertWindDirectionCountsToPackedBytes(summary.WindDirectionCounts);
					dirCountParam.Size = data.Length;

					command.ExecuteNonQuery();
					counter++;
					if (counter >= RecordBatchQuantity) {
						counter = 0;
						command.Transaction.Commit();
						command.Transaction = _connection.BeginTransaction();
					}
				}
				command.Transaction.Commit();

			}
			return true;
		}

		private static DateTime PeriodTenMinutes(DateTime time) {
			return new DateTime(time.Year,time.Month,time.Day, time.Hour, (time.Minute / 10) * 10,0);
		}

		private bool UpdateSummaryRecords(PosixTimeRange rangeCovered, string sensorName, int sensorId) {
			return UpdateSummaryRecords(
				new TimeRange(rangeCovered.Low.AsDateTime, rangeCovered.High.AsDateTime),
				sensorName, sensorId);
		}

		private bool UpdateSummaryRecords(TimeRange rangeCovered, string sensorName, int sensorId) {
			TimeRange tenMinuteAlignedRange = new TimeRange(
				UnitUtility.StripToUnit(rangeCovered.Low, TimeUnit.TenMinutes),
				UnitUtility.StripToUnit(rangeCovered.High, TimeUnit.TenMinutes) + TenMinutes
			);
			var minuteSummaries = StatsUtil.Summarize(
				GetReadings(sensorName, tenMinuteAlignedRange.Low, tenMinuteAlignedRange.Span), TimeUnit.Minute
			).ToList();
			bool minsOk = PushSummaries(sensorId, "MinuteRecord", minuteSummaries, new PosixTimeRange(tenMinuteAlignedRange));

			var tenMinuteSummaries = new List<ReadingsSummary>();
			{
				List<List<ReadingsSummary>> tenMinutePeriodSummaries = new List<List<ReadingsSummary>>();
				List<ReadingsSummary> currentMinuteSummaries = new List<ReadingsSummary>();
				foreach (ReadingsSummary minuteSummary in minuteSummaries) {
					if (currentMinuteSummaries.Count == 0
						|| PeriodTenMinutes(currentMinuteSummaries[0].BeginStamp).Equals(PeriodTenMinutes(minuteSummary.BeginStamp))
					) {
						currentMinuteSummaries.Add(minuteSummary);
					}
					else {
						tenMinutePeriodSummaries.Add(currentMinuteSummaries);
						currentMinuteSummaries = new List<ReadingsSummary>();
					}
				}
				if (currentMinuteSummaries.Count > 0) {
					tenMinutePeriodSummaries.Add(currentMinuteSummaries);
				}
				foreach (List<ReadingsSummary> minutelyForCondensing in tenMinutePeriodSummaries) {
					ReadingsSummary summary = StatsUtil.Combine(minutelyForCondensing);
					summary.BeginStamp = PeriodTenMinutes(summary.BeginStamp);
					summary.EndStamp = summary.BeginStamp.Add(TenMinutes).AddTicks(-1);
					tenMinuteSummaries.Add(summary);
				}
			}

			bool tenMinsOk = PushSummaries(sensorId, "TenminuteRecord", tenMinuteSummaries, new PosixTimeRange(tenMinuteAlignedRange));

			return minsOk && tenMinsOk;
		}

		public bool DeleteSensor(string name) {
			if (!ForceConnectionOpen()) {
				throw new Exception("Could not open database.");
			}

			// find the sensor ID
			int sensorIdKey;
			using (var command = _connection.CreateCommand()) {
				command.CommandType = CommandType.Text;
				command.CommandText = "SELECT sensorId FROM Sensor WHERE nameKey=@nameKey";
				var sensorNameParam = command.CreateParameter() as DbParameter;
				sensorNameParam.Value = name;
				sensorNameParam.DbType = DbType.String;
				sensorNameParam.ParameterName = "nameKey";
				command.Parameters.Add(sensorNameParam);
				using (var reader = command.ExecuteReader()) {
					if (reader.Read()) {
						sensorIdKey = reader.GetInt32(0);
					}
					else {
						return false;
					}
				}
			}

			bool result = true;

			// purge all data from tables
			using (var command = _connection.CreateCommand()) {
				var sensorId = command.CreateParameter() as DbParameter;
				sensorId.DbType = DbType.Int32;
				sensorId.ParameterName = "sensorID";
				sensorId.Value = sensorIdKey;
				command.Parameters.Add(sensorId);
				command.CommandType = CommandType.Text;
				foreach (var tableName in new[] { "hourrecord", "dayrecord", "record", "sensor" }) {
					command.CommandText = String.Format("DELETE FROM {0} WHERE sensorID=@sensorID", tableName);
					try {
						command.ExecuteNonQuery();
					}
					catch {
						result = false;
					}
				}
			}

			return result;
		}

		#endregion

		#region Summary CRUD


		private IEnumerable<T> GetPackedSummaries<T>(string sensor, DateTime from, TimeSpan span, string tableName)
			where T:PackedReadingsSummary, new()
		{
			DateTime to = from.Add(span);
			if (to < from) {
				DateTime s = to;
				to = from;
				from = s;
			}
			if (!ForceConnectionOpen()) {
				throw new Exception("Could not open database.");
			}
			using (var command = _connection.CreateTextCommand(String.Format(
				"SELECT stamp,minValues,maxValues,meanValues,stddevValues,recordCount"
				+ ",tempCount,pressCount,humCount,speedCount,dirCount"
				+ " FROM [{0}]"
				+ " INNER JOIN Sensor ON (Sensor.sensorId = [{0}].sensorId)"
				+ " WHERE Sensor.nameKey = @sensorNameKey"
				+ " AND [{0}].stamp >= @minPosixStamp"
				+ " AND [{0}].stamp < @maxPosixStamp"
				+ " ORDER BY stamp " + ((span < TimeSpan.Zero) ? "DESC" : "ASC"),
				tableName
			))) {
				command.AddParameter("minPosixStamp", DbType.Int32, UnitUtility.ConvertToPosixTime(from));
				command.AddParameter("maxPosixStamp", DbType.Int32, UnitUtility.ConvertToPosixTime(to));
				command.AddParameter("sensorNameKey", DbType.String, sensor);

				using (IDataReader reader = command.ExecuteReader()) {
					int ordStamp = reader.GetOrdinal("stamp");
					int ordMinValues = reader.GetOrdinal("minValues");
					int ordMaxValues = reader.GetOrdinal("maxValues");
					int ordMeanValues = reader.GetOrdinal("meanValues");
					int ordStddevValues = reader.GetOrdinal("stddevValues");
					int ordRecordCount = reader.GetOrdinal("recordCount");
					int ordTempCount = reader.GetOrdinal("tempCount");
					int ordPressCount = reader.GetOrdinal("pressCount");
					int ordHumCount = reader.GetOrdinal("humCount");
					int ordSpeedCount = reader.GetOrdinal("speedCount");
					int ordDirCount = reader.GetOrdinal("dirCount");

					byte[] values = new byte[8];
					//return ReadAsSensorReadings(reader);
					while (reader.Read()) {

						reader.GetBytes(ordMinValues, 0, values, 0, values.Length);
						PackedReadingValues minValues = PackedReadingValues.ConvertFromPackedBytes(values);
						reader.GetBytes(ordMaxValues, 0, values, 0, values.Length);
						PackedReadingValues maxValues = PackedReadingValues.ConvertFromPackedBytes(values);
						reader.GetBytes(ordMeanValues, 0, values, 0, values.Length);
						PackedReadingValues meanValues = PackedReadingValues.ConvertFromPackedBytes(values);
						reader.GetBytes(ordStddevValues, 0, values, 0, values.Length);
						PackedReadingValues stddevValues = PackedReadingValues.ConvertFromPackedBytes(values);
						var summary = new T {
						    BeginStamp = UnitUtility.ConvertFromPosixTime(reader.GetInt32(ordStamp)),
						    Min = minValues,
						    Max = maxValues,
						    Mean = meanValues,
						    SampleStandardDeviation = stddevValues,
						    Count = reader.GetInt32(ordRecordCount),
						    TemperatureCounts =
						        PackedReadingValues.PackedCountsToHashUnsigned16(reader.GetValue(ordTempCount) as byte[]),
						    PressureCounts =
						        PackedReadingValues.PackedCountsToHashUnsigned16(reader.GetValue(ordPressCount) as byte[]),
						    HumidityCounts =
						        PackedReadingValues.PackedCountsToHashUnsigned16(reader.GetValue(ordHumCount) as byte[]),
						    WindSpeedCounts =
						        PackedReadingValues.PackedCountsToHashUnsigned16(reader.GetValue(ordSpeedCount) as byte[]),
						    WindDirectionCounts =
						        PackedReadingValues.PackedCountsToHashUnsigned16(reader.GetValue(ordDirCount) as byte[])
						};
						yield return summary;


					}
				}

			}
		}

		private static TimeSpan ChooseBestSummaryTimeSpan(TimeSpan desiredTimeSpan) {
			if(ValidSummaryTimeSpans.Length > 0) {
				int lasti = ValidSummaryTimeSpans.Length - 1;
				if(desiredTimeSpan <= ValidSummaryTimeSpans[0]) {
					return ValidSummaryTimeSpans[0];
				}
				if (desiredTimeSpan >= ValidSummaryTimeSpans[lasti]) {
					return ValidSummaryTimeSpans[lasti];
				}
				for (int i = 0, nexti = 1; i < lasti; i = nexti++) {
					if (ValidSummaryTimeSpans[i] <= desiredTimeSpan) {
						long distI = Math.Abs(ValidSummaryTimeSpans[i].Ticks - desiredTimeSpan.Ticks);
						long distNextI = Math.Abs(ValidSummaryTimeSpans[nexti].Ticks - desiredTimeSpan.Ticks);
						if(distI < distNextI) {
							return ValidSummaryTimeSpans[i];
						}
						return ValidSummaryTimeSpans[nexti];
					}
				}

			}
			return default(TimeSpan);
		}

		public IEnumerable<IReadingsSummary> GetReadingSummaries(string sensor, DateTime from, TimeSpan span, TimeSpan desiredSummaryUnitSpan) {

			var bestTimeSpan = ChooseBestSummaryTimeSpan(desiredSummaryUnitSpan);

			if(bestTimeSpan == OneMinute) {
				return GetPackedSummaries<PackedReadingsMinuteSummary>(sensor, from, span, "MinuteRecord").Cast<IReadingsSummary>();
			}
			if (bestTimeSpan == TenMinutes) {
				return GetPackedSummaries<PackedReadingsTenMinutesSummary>(sensor, from, span, "TenminuteRecord").Cast<IReadingsSummary>();
			}
			throw new NotSupportedException(String.Format("Summaries for {0} are not supported.",bestTimeSpan));
		}

		public bool RenameSensor(string currentName, string desiredName) {
			try {
				if (!ForceConnectionOpen())
					throw new Exception("Could not open database.");

				using (var command = _connection.CreateTextCommand("UPDATE Sensor SET nameKey=@newNameKey WHERE nameKey=@oldNameKey")) {
					command.AddParameter("newNameKey", DbType.String, desiredName);
					command.AddParameter("oldNameKey", DbType.String, currentName);
					command.ExecuteNonQuery();
				}
				return true;
			} catch {
				return false;
			}
		}

		public IEnumerable<TimeSpan> SupportedSummaryUnitSpans {
			get {
				yield return OneMinute;
				yield return TenMinutes;
				yield return OneHour;
				yield return OneDay;
			}
		}

		#endregion

		#region Connection and Utility methods

		private bool ForceConnectionOpen() {
			if (_connection.State == ConnectionState.Closed) {
				_connection.Open();
				return _connection.State == ConnectionState.Open || _connection.State == ConnectionState.Connecting;
			}
			return true;
		}

		public void Dispose() {
			Dispose(true);
		}

		public virtual void Dispose(bool disposing) {
			if(disposing) {
				; // managed stuff here
			}
			if (null != _connection) {
				_connection.Dispose();
			}
		}

		#endregion


	}
}
