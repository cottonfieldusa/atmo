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
using System.Collections.ObjectModel;
using Atmo.Test;
using Atmo.Units;
using System.Linq;

namespace Atmo.Demo {
	public class DemoDaqConnection : KeyedCollection<string, DemoDaqConnection.DemoSensor>, IDaqConnection {

		public class DemoSensor : ISensor, ISensorInfo {

			private DemoDaqConnection _parent;
			private string _name;
			private ReadingValues _lastReading;
			private Random _rand = new Random((int)System.DateTime.Now.Ticks);
			private bool _valid;

			public DemoSensor(string name, DemoDaqConnection parent, bool valid) {
				_name = name;
				_parent = parent;
				_valid = valid;
			}

			public string Name {
				get { return _name; }
			}

			public SpeedUnit SpeedUnit {
				get { return SpeedUnit.MetersPerSec; }
			}

			public TemperatureUnit TemperatureUnit {
				get { return TemperatureUnit.Celsius; }
			}

			public PressureUnit PressureUnit {
				get { return PressureUnit.Millibar; }
			}

			public IReading GetCurrentReading() {
				var now = _parent.QueryClock();
				var reading = GetCurrentReading(now, _rand, _lastReading);
				_lastReading = new ReadingValues(reading);
				return reading;
			}

			public bool IsValid {
				get { return _valid; }
				set { _valid = value; }
			}

			public static PackedReading GetCurrentReading(DateTime now, Random rand, IReadingValues lastReading) {

				double averagePressure = 1014.23;
				double pressureRange = 100.0;

				double pressure = (rand.NextDouble() * pressureRange) - (pressureRange / 2) + averagePressure;
				double windDirectionRad = (rand.NextDouble() * Math.PI * 2.0);


				//double windDirectionDeg = Vector2D.RadiansToDegrees(windDirectionRad);
				Vector2D windDirectionVec = new Vector2D((Math.Cos(windDirectionRad) * 2.0), (Math.Sin(windDirectionRad) * 2.0));
				double windDirectionDeg = windDirectionVec.GetNorthRelativeClockwiseAngularDegrees();


				double averageWindSpeed = 9;
				double averageTemp = 51.0;
				double tempRange = 10.0;
				double averageHumM = 79;
				double averageHumA = 58;

				switch (now.Month) {
				case 1: {
						averageWindSpeed = 10.4;
						averageTemp = 27.5;
						averageHumM = 77;
						averageHumA = 66;
						break;
					}
				case 2: {
						averageWindSpeed = 10.3;
						averageTemp = 30.5;
						averageHumM = 75;
						averageHumA = 62;
						break;
					}
				case 3: {
						averageWindSpeed = 10.6;
						averageTemp = 39.8;
						averageHumM = 76;
						averageHumA = 57;
						break;
					}
				case 4: {
						averageWindSpeed = 10.2;
						averageTemp = 49.9;
						averageHumM = 74;
						averageHumA = 51;
						break;
					}
				case 5: {
						averageWindSpeed = 8.7;
						averageTemp = 60.0;
						averageHumM = 77;
						averageHumA = 52;
						break;
					}
				case 6: {
						averageWindSpeed = 8;
						averageTemp = 68.4;
						averageHumM = 80;
						averageHumA = 53;
						break;
					}
				case 7: {
						averageWindSpeed = 7.3;
						averageTemp = 72.6;
						averageHumM = 83;
						averageHumA = 54;
						break;
					}
				case 8: {
						averageWindSpeed = 6.8;
						averageTemp = 71;
						averageHumM = 86;
						averageHumA = 56;
						break;
					}
				case 9: {
						averageWindSpeed = 7.4;
						averageTemp = 64.0;
						averageHumM = 87;
						averageHumA = 57;
						break;
					}
				case 10: {
						averageWindSpeed = 8.3;
						averageTemp = 52.5;
						averageHumM = 82;
						averageHumA = 55;
						break;
					}
				case 11: {
						averageWindSpeed = 9.7;
						averageTemp = 42.3;
						averageHumM = 79;
						averageHumA = 62;
						break;
					}
				case 12: {
						averageWindSpeed = 10.1;
						averageTemp = 32.5;
						averageHumM = 78;
						averageHumA = 67;
						break;
					}
				}
				averageTemp = UnitUtility.ConvertUnit(averageTemp, TemperatureUnit.Fahrenheit, TemperatureUnit.Celsius);
				tempRange = 4;
				averageWindSpeed = UnitUtility.ConvertUnit(averageWindSpeed, SpeedUnit.MilesPerHour, SpeedUnit.MetersPerSec) * 4.0;

				double humidityRange = Math.Abs(averageHumM - averageHumA) * 1.5;
				double humidity = (humidityRange * rand.NextDouble()) - (humidityRange / 2.0) + ((averageHumM + averageHumA) / 2.0);
				humidity /= 100.0;
				double temp = (rand.NextDouble() * tempRange) - (tempRange / 2.0) + averageTemp;

				double windSpeed = rand.NextDouble();
				windSpeed = (windSpeed > 0.5) ? 0 : 4.0 * averageWindSpeed * windSpeed * windSpeed;
				if (null != lastReading && lastReading.IsValid) {
					pressure = (pressure + pressure + lastReading.Pressure) / 3.0;
					double oldWindDirRad = (lastReading.WindDirection / 180.0) * Math.PI;
					oldWindDirRad = -oldWindDirRad;
					oldWindDirRad += Math.PI / 2.0;

					var oldWindDirVec = new Vector2D(windDirectionVec.X + (Math.Cos(oldWindDirRad) * 10.0), windDirectionVec.Y + (Math.Sin(oldWindDirRad) * 10.0));
					windDirectionDeg = oldWindDirVec.GetNorthRelativeClockwiseAngularDegrees();
					windSpeed = (windSpeed + windSpeed + lastReading.WindSpeed * 3.0) / 5.0;
					temp = (temp + temp + (lastReading.Temperature * 20.0)) / 22.0;
					humidity = (humidity + humidity + lastReading.Humidity * 80.0) / 82.0;
				}

				var newValues = new ReadingValues(
					temp,
					pressure,
					humidity,
					windSpeed,
					windDirectionDeg
				);
				var stamp = now.Date.Add(new TimeSpan(now.Hour, now.Minute, now.Second));
				return new PackedReading(
					stamp,
					new PackedReadingValues(newValues)
				);
			}
		}

		public DemoDaqConnection() {
            Add(new DemoSensor("A", this, true));
            Add(new DemoSensor("B", this, true));
            Add(new DemoSensor("C", this, false));
            Add(new DemoSensor("D", this, false));
        }

		protected override string GetKeyForItem(DemoSensor item) {
			return item.Name;
		}

		public DateTime QueryClock() {
			var now = DateTime.Now;
			return now.Date.Add(new TimeSpan(now.Hour, now.Minute, now.Second));
		}

		public TimeSpan Ping() {
			var start = DateTime.Now;
			System.Threading.Thread.Sleep(65);
			return DateTime.Now.Subtract(start);
		}

		public bool SetClock(DateTime time) {
			; // do nothing
			return true;
		}

		public ISensor GetSensor(int i) {
			if (i < 0 || i >= Count) {
				return null;
			}
			return this[i];
		}


		IEnumerator<ISensor> IEnumerable<ISensor>.GetEnumerator() {
			foreach(var item in this) {
				yield return item;
			}
		}

	}
}