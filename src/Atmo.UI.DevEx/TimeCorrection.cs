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
using System.Windows.Forms;
using Atmo;
using Atmo.Data;
using System.Linq;

namespace Atmo.UI.DevEx {
    public partial class TimeCorrection : DevExpress.XtraEditors.XtraForm {

        private readonly IDataStore _dataStore;
    	private const bool _overwrite = true;
        
        public TimeCorrection(IDataStore dataStore) {
            _dataStore = dataStore;
            InitializeComponent();
        }

        private void cancelButton_Click(object sender, EventArgs e) {
            Close();
        }

        private void adjustButton_Click(object sender, EventArgs e) {

        	var sensorName = sensorNameSelector.EditValue as string;

			if(String.IsNullOrEmpty(sensorName)) {
				MessageBox.Show("You must specify a sensor. No action has been taken.", "Invalid operation.");
				return;
			}

        	var sourceTimeRange = new TimeRange(dateTimeRangePickerData.From, dateTimeRangePickerData.To);
			var spanOffset = dateTimePickerNewStart.DateTime - sourceTimeRange.Low;
			if (sourceTimeRange.Span == TimeSpan.Zero) {
				MessageBox.Show("You must specify a source time range. No action has been taken.", "Invalid operation.");
				return;
			}
			if(spanOffset == TimeSpan.Zero) {
				MessageBox.Show("You must specify a time offset for the selected region. No action has been taken.", "Invalid operation.");
				return;
			}
        	var offsetTimeRange = new TimeRange(sourceTimeRange.Low + spanOffset, sourceTimeRange.High + spanOffset);

        	var adjustOk = _dataStore.AdjustTimeStamps(
				sensorName,
				sourceTimeRange,
				offsetTimeRange,
				_overwrite
        	);
            if (adjustOk) {
                MessageBox.Show(
					"Correction applied.", "Success",
					MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            else {
                MessageBox.Show(
					"Correction failed.", "Failed",
					MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void TimeCorrection_Load(object sender, EventArgs e) {
            sensorNameSelector.Properties.Items.Clear();
            foreach (string name in _dataStore.GetAllSensorInfos().Select(si => si.Name)) {
                sensorNameSelector.Properties.Items.Add(name);
            }
        }

    }
}