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

namespace Atmo {
	public struct TimeStampedValue : IComparable<TimeStampedValue> {

		public readonly DateTime Stamp;
		public readonly double Value;

		public TimeStampedValue(DateTime stamp, double value) {
			Stamp = stamp;
			Value = value;
		}

		public int CompareTo(TimeStampedValue other) {
			var c = Stamp.CompareTo(other.Stamp);
			return 0 != c ? c : Value.CompareTo(other.Value);
		}

		public DateTime StampProperty {
			get { return Stamp; }
		}

		public double ValueProperty {
			get { return Value; }
		}
	}
}
