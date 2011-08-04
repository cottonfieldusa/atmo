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
using System.Linq;
using Atmo.Daq.Win32;
using Atmo.Data;
using Atmo.Stats;
using Atmo.UI.DevEx.Controls;
using Atmo.UI.WinForms.Controls;
using Atmo.Units;
using DevExpress.XtraEditors;
using System.Windows.Forms;

namespace Atmo.UI.DevEx {
	public partial class MainForm : XtraForm {

		private IDaqConnection _deviceConnection = null;
		private MemoryDataStore _memoryDataStore = null;
		private SensorViewPanelController _sensorViewPanelControler = null;
		private HistoricSensorViewPanelController _historicSensorViewPanelController = null;
		private System.Data.SQLite.SQLiteConnection _dbConnection = null;
		private IDataStore _dbStore;
		private bool _updateHistorical = false;

		private ProgramContext AppContext { get; set; }

		public MainForm(ProgramContext appContext) {
			if (null == appContext) {
				throw new ArgumentNullException();
			}
			AppContext = appContext;

			InitializeComponent();

			ConverterCache = ReadingValuesConverterCache<IReadingValues, ReadingValues>.Default;
			ConverterCacheReadingValues = ReadingValuesConverterCache<ReadingValues>.Default;
			ConverterCacheReadingAggregate = ReadingValuesConverterCache<ReadingAggregate>.Default;
			liveAtmosphericGraph.ConverterCacheReadingValues = ConverterCacheReadingValues;

			_deviceConnection = new Demo.DemoDaqConnection();

			_dbConnection = new System.Data.SQLite.SQLiteConnection(
				@"data source=ClearStorage.db;page size=4096;cache size=4000;journal mode=Off"
			);
			_dbStore = new DbDataStore(_dbConnection);

			_memoryDataStore = new MemoryDataStore();

			_sensorViewPanelControler = new SensorViewPanelController(groupControlSensors) {
				DefaultSelected = true
			};
			_historicSensorViewPanelController = new HistoricSensorViewPanelController(groupControlDbList) {
				DefaultSelected = true,
			};
			_historicSensorViewPanelController.OnDeleteRequested += OnDeleteRequested;

			historicalTimeSelectHeader.CheckEdit.CheckedChanged += histNowChk_CheckedChanged;
			ReloadHistoric();

			historicalTimeSelectHeader.TimeRange.SelectedIndex = historicalTimeSelectHeader.TimeRange.FindNearestIndex(AppContext.PersistentState.HistoricalTimeScale);
			liveAtmosphericHeader.TimeRange.SelectedIndex = liveAtmosphericHeader.TimeRange.FindNearestIndex(AppContext.PersistentState.LiveTimeScale);

			historicalTimeSelectHeader.TimeRange.ValueChanged += historicalTimeSelectHeader_TimeRangeValueChanged;
			liveAtmosphericHeader.TimeRange.ValueChanged += liveTimeSelectHeader_TimeRangeValueChanged;

			foreach(var view in _historicSensorViewPanelController.Views) {
				bool selected = false;
				if(
					null != view && null != view.SensorInfo
					&& AppContext.PersistentState.SelectedDatabases.Contains(view.SensorInfo.Name)
				) {
					selected = true;
				}
				view.IsSelected = selected;
			}
		}

		public void historicalTimeSelectHeader_TimeRangeValueChanged(object sender, EventArgs args) {
			AppContext.PersistentState.HistoricalTimeScale = historicalTimeSelectHeader.TimeRange.SelectedSpan;
			AppContext.PersistentState.IsDirty = true;
		}

		public void liveTimeSelectHeader_TimeRangeValueChanged(object sender, EventArgs args) {
			AppContext.PersistentState.LiveTimeScale = liveAtmosphericHeader.TimeRange.SelectedSpan;
			AppContext.PersistentState.IsDirty = true;
		}

		public TemperatureUnit TemperatureUnit { get { return AppContext.PersistentState.TemperatureUnit; } }
		public SpeedUnit SpeedUnit { get { return AppContext.PersistentState.SpeedUnit; } }
		public PressureUnit PressureUnit { get { return AppContext.PersistentState.PressureUnit; } }

		public ReadingValuesConverterCache<IReadingValues, ReadingValues> ConverterCache { get; set; }
		public ReadingValuesConverterCache<ReadingValues> ConverterCacheReadingValues { get; set; }
		public ReadingValuesConverterCache<ReadingAggregate> ConverterCacheReadingAggregate { get; set; }

		private void OnDeleteRequested(ISensorInfo sensorInfo) {
			if (null == sensorInfo || String.IsNullOrEmpty(sensorInfo.Name) || null == _dbStore) {
				MessageBox.Show("Invalid target", "Error");
				return;
			}

			var result = MessageBox.Show(
				String.Format("Are you sure you want to delete the sensor named '{0}'?", sensorInfo.Name), "Delete?",
			    MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question
			);
			if(result != DialogResult.Yes) {
				return;
			}
			result = MessageBox.Show(
				String.Format("Are you really sure you want to delete the sensor named '{0}'? All data for this sensor will be removed without possibility of retrieval.", sensorInfo.Name), "DELETE?",
				MessageBoxButtons.YesNoCancel, MessageBoxIcon.Exclamation
			);
			if (result != DialogResult.Yes) {
				return;
			}
			_dbStore.DeleteSensor(sensorInfo.Name);
			ReloadHistoric();
		}

		private void timerTesting_Tick(object sender, EventArgs e) {
			
			// current live state
			var now = DateTime.Now;
			var sensors = _deviceConnection.Where(s => s.IsValid).ToList();

			// get readings
			var readings = new Dictionary<ISensor, IReading>();
			foreach (var sensor in sensors) {
				var reading = sensor.GetCurrentReading();
				if(reading.IsValid) {
					readings.Add(sensor, reading);
				}
			}

			// save to memory
			foreach(var reading in readings) {
				if(!_memoryDataStore.GetAllSensorInfos().Any(si => si.Name.Equals(reading.Key.Name))) {
					_memoryDataStore.AddSensor(reading.Key);
				}
				_memoryDataStore.Push(reading.Key.Name, new [] { reading.Value });
			}

			// update the sensor controls
			_sensorViewPanelControler.UpdateView(sensors);

			// the current sensor views
			var sensorViews = _sensorViewPanelControler.Views.ToList();

			// determine which sensors are enabled
			var enabledSensors = new List<ISensor>();
			for(int i = 0; i < sensors.Count && i < sensorViews.Count; i++) {
				if(sensorViews[i].IsSelected) {
					enabledSensors.Add(sensors[i]);
				}
			}
			
			var liveDataEnabled = true;
			var liveDataTimeSpan = liveAtmosphericHeader.TimeRange.SelectedSpan;

			// pass it off to the live data graphs/tables
			if(liveDataEnabled) {
				
				// gather the data for each selected sensor
				var enabledSensorsLiveMeans = new List<List<ReadingAggregate>>(enabledSensors.Count);
				foreach(var sensor in enabledSensors) {
					
					// get the recent readings
					var recentReadings = _memoryDataStore.GetReadings(sensor.Name, now, TimeSpan.Zero.Subtract(liveDataTimeSpan));
					
					// calculate the mean data
					var means = StatsUtil.AggregateMean(recentReadings, TimeUnit.Second).ToList();

					// convert the units for display
					var converter = ConverterCacheReadingAggregate.Get(
						sensor.TemperatureUnit, TemperatureUnit,
						sensor.SpeedUnit, SpeedUnit,
						sensor.PressureUnit, PressureUnit
					);
					converter.ConvertInline(means);
					
					// add it to the presentation list
					enabledSensorsLiveMeans.Add(means);
				}

				// compile it all together into one set
				var enabledSensorsCompiledMeans = StatsUtil.JoinParallelMeanReadings(enabledSensorsLiveMeans);

				// present the data set
				liveAtmosphericGraph.TemperatureUnit = TemperatureUnit;
				liveAtmosphericGraph.PressureUnit = PressureUnit;
				liveAtmosphericGraph.SpeedUnit = SpeedUnit;
				liveAtmosphericGraph.FormatTimeAxis(liveDataTimeSpan);
				liveAtmosphericGraph.SetLatest(enabledSensorsCompiledMeans.LastOrDefault());
				liveAtmosphericGraph.State = AppContext.PersistentState;
				liveAtmosphericGraph.SetDataSource(enabledSensorsCompiledMeans);
			}

			var histTimeRangeSelector = historicalTimeSelectHeader.TimeRange;
			var histDateChooser = historicalTimeSelectHeader.DateEdit;
			var histNowChk = historicalTimeSelectHeader.CheckEdit;
			var histTimeChooser = historicalTimeSelectHeader.TimeEdit;

			var histTimeSpan = histTimeRangeSelector.SelectedSpan;
			var histStartDate = histNowChk.Checked
			    ? now.Subtract(histTimeSpan)
			    : histDateChooser.DateTime.Date.Add(histTimeChooser.Time.TimeOfDay);

			var cumTimeInfo = HistoricalGraphBreakdown.GetCumulativeWindows(
				histStartDate.Add(histTimeSpan),
				histTimeSpan,
				!histNowChk.Checked
			);

			var historicalSelected = _dbStore.GetAllSensorInfos().Where(si => _historicSensorViewPanelController.IsSensorSelected(si)).ToList();
			var sensorReadings = historicalSelected.Select(
				sensor =>
				_dbStore.GetReadingSummaries(sensor.Name, cumTimeInfo.MaxStamp, cumTimeInfo.MinStamp - cumTimeInfo.MaxStamp, UnitUtility.ChooseBestUnit(histTimeSpan))
			);

			var historicalSummaries = StatsUtil.JoinReadingSummaryEnumerable(sensorReadings).ToList();

			windResourceGraph.TemperatureUnit = TemperatureUnit;
			windResourceGraph.PressureUnit = PressureUnit;
			windResourceGraph.SpeedUnit = SpeedUnit;

			windResourceGraph.SetDataSource(historicalSummaries);

			historicalGraphBreakdown.TemperatureUnit = TemperatureUnit;
			historicalGraphBreakdown.PressureUnit = PressureUnit;
			historicalGraphBreakdown.SpeedUnit = SpeedUnit;
			historicalGraphBreakdown.SelectedAttributeType = ReadingAttributeType.WindSpeed;
			historicalGraphBreakdown.StepBack = !histNowChk.Checked;
			historicalGraphBreakdown.DrillStartDate = histStartDate;
			historicalGraphBreakdown.CumulativeTimeSpan = histTimeSpan;

			historicalGraphBreakdown.SetDataSource(historicalSummaries);
			


		}

		private void barButtonItemPrefs_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			var settingsForm = new SettingsForm(AppContext.PersistentState);
			settingsForm.ShowDialog(this);
		}

		private void simpleButtonFindSensors_Click(object sender, EventArgs e) {
			FindSensors();
		}

		private void barButtonItemSensorSetup_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			FindSensors();
		}

		private void FindSensors() {
			var findSensorForm = new FindSensorsDialog(_deviceConnection);
			findSensorForm.ShowDialog(this);
		}

		private void barButtonItemImport_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			DownloadDataDialog();
		}

		private void simpleButtonDownloadData_Click(object sender, EventArgs e) {
			DownloadDataDialog(true);
		}

		private void DownloadDataDialog(bool auto = false) {
			var importForm = new ImportDataForm(_dbStore, _deviceConnection) {
				AutoImport = true
			};
			importForm.ShowDialog(this);
			ReloadHistoric();
		}

		public void ReloadHistoric() {
			var historicSensors = _dbStore.GetAllSensorInfos();
			_historicSensorViewPanelController.UpdateView(historicSensors);
			TriggerHistoricalUpdate();
		}

		private void TriggerHistoricalUpdate() {
			_updateHistorical = true;
		}

		private void histNowChk_CheckedChanged(object sender, EventArgs e) {
			TriggerHistoricalUpdate();
		}

		private void barButtonItemTimeCorrection_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			var timeCorrectionDialog = new TimeCorrection(_dbStore);
			timeCorrectionDialog.ShowDialog(this);
		}

		private void barButtonItemExport_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			var exportForm = new ExportForm(_dbStore);
			exportForm.ShowDialog(this);
		}

		private void barButtonItemTimeSync_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			var timeSync = new TimeSync(_deviceConnection,_dbStore);
			timeSync.ShowDialog(this);
		}

		private void barButtonItemExit_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			Close();
		}

		private void barButtonItemFirmwareUpdate_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			if(!(_deviceConnection is UsbDaqConnection)) {
				MessageBox.Show("Device is not supported", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}
			var patchForm = new PatcherForm(_deviceConnection as UsbDaqConnection);
			patchForm.ShowDialog();
		}

		private void MainForm_FormClosed(object sender, FormClosedEventArgs e) {
			
		}

		private void MainForm_FormClosing(object sender, FormClosingEventArgs e) {
			if(null != AppContext) {
				var selectedNames = _historicSensorViewPanelController.Views
					.Where(v => v.IsSelected && null != v.SensorInfo)
					.Select(v => v.SensorInfo.Name)
					.ToList();

				AppContext.PersistentState.SelectedDatabases = selectedNames;
				AppContext.PersistentState.IsDirty = true;
			}
		}


	}
}
