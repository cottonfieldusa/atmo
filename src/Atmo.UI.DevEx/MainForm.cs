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
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using Atmo.Calculation;
using Atmo.Daq.Win32;
using Atmo.Data;
using Atmo.Stats;
using Atmo.UI.DevEx.Controls;
using Atmo.UI.WinForms.Controls;
using Atmo.Units;
using DevExpress.XtraEditors;
using System.Windows.Forms;
using log4net;

namespace Atmo.UI.DevEx {
	public partial class MainForm : XtraForm
	{

		private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
		private static readonly string DatabaseFileName = "ClearStorage.db";

		private IDaqConnection _deviceConnection = null;
		private MemoryDataStore _memoryDataStore = null;
		private SensorViewPanelController _sensorViewPanelControler = null;
		private HistoricSensorViewPanelController _historicSensorViewPanelController = null;
		private System.Data.SQLite.SQLiteConnection _dbConnection = null;
		private IDataStore _dbStore;
		private bool _updateHistorical = false;
		private object _historicalUpdateThreadMutex = new object();
		private DateTime _lastLiveUpdate = default(DateTime);
		private TimeSpan _lastLiveUpdateInterval = default(TimeSpan);

		private ProgramContext AppContext { get; set; }

		public MainForm(ProgramContext appContext) {
			if (null == appContext) {
				throw new ArgumentNullException();
			}

			AppContext = appContext;
			InitializeComponent();

			Text = ProgramContext.ProgramFriendlyName;

			ConverterCache = ReadingValuesConverterCache<IReadingValues, ReadingValues>.Default;
			ConverterCacheReadingValues = ReadingValuesConverterCache<ReadingValues>.Default;
			ConverterCacheReadingAggregate = ReadingValuesConverterCache<ReadingAggregate>.Default;
			liveAtmosphericGraph.ConverterCacheReadingValues = ConverterCacheReadingValues;

			_deviceConnection = new UsbDaqConnection(); // new Demo.DemoDaqConnection();

			// create the DB file using the application privilages instead of using the installer
			if (!File.Exists(DatabaseFileName)) {
				using(var dbTemplateDataStream = typeof (DbDataStore).Assembly.GetManifestResourceStream("Atmo." + DatabaseFileName))
				using(var outDbTemplate = File.Create(DatabaseFileName)) {
					var buffer = new byte[1024];
					int totalRead;
					while(0 < (totalRead = dbTemplateDataStream.Read(buffer, 0, buffer.Length)))
						outDbTemplate.Write(buffer,0, totalRead);
				}
				
				Thread.Sleep(250); // just to be safe
				Log.InfoFormat("Core DB file created at: {0}", Path.GetFullPath(DatabaseFileName));
			}
			try {
				_dbConnection = new System.Data.SQLite.SQLiteConnection(
					@"data source=" + DatabaseFileName + @";page size=4096;cache size=4000;journal mode=Off");
				_dbConnection.Open();
			}
			catch(Exception ex) {
				Log.Error("Database connection failure.", ex);
				throw;
			}
			_dbStore = new DbDataStore(_dbConnection);

			_memoryDataStore = new MemoryDataStore();

			_sensorViewPanelControler = new SensorViewPanelController(groupControlSensors) {
				DefaultSelected = true
			};
			_historicSensorViewPanelController = new HistoricSensorViewPanelController(groupControlDbList) {
				DefaultSelected = true,
			};
			_historicSensorViewPanelController.OnDeleteRequested += OnDeleteRequested;
			_historicSensorViewPanelController.OnRenameRequested += OnRenameRequested;

			historicalTimeSelectHeader.CheckEdit.CheckedChanged += histNowChk_CheckedChanged;

			historicalTimeSelectHeader.TimeRange.SelectedIndex = historicalTimeSelectHeader.TimeRange.FindNearestIndex(AppContext.PersistentState.HistoricalTimeScale);
			liveAtmosphericHeader.TimeRange.SelectedIndex = liveAtmosphericHeader.TimeRange.FindNearestIndex(AppContext.PersistentState.LiveTimeScale);

			historicalTimeSelectHeader.TimeRange.ValueChanged += historicalTimeSelectHeader_TimeRangeValueChanged;
			liveAtmosphericHeader.TimeRange.ValueChanged += liveTimeSelectHeader_TimeRangeValueChanged;

			foreach(var view in _historicSensorViewPanelController.Views.Where(v => null != v)) {
				bool selected = false;
				if(
					null != view.SensorInfo
					&& AppContext.PersistentState.SelectedDatabases.Contains(view.SensorInfo.Name)
				) {
					selected = true;
				}
				view.IsSelected = selected;
			}

			HandleRapidFireSetup();
			HandleDaqTemperatureSourceSet(_deviceConnection.UsingDaqTemp);

			ReloadHistoric();
			_historicSensorViewPanelController.OnSelectionChanged += RequestHistoricalUpdate;
			historicalGraphBreakdown.OnSelectedPropertyChanged += RequestHistoricalUpdate;
			historicalTimeSelectHeader.OnTimeRangeChanged += RequestHistoricalUpdate;
			windResourceGraph.OnWeibullParamChanged += RequestHistoricalUpdate;
#if DEBUG
			barSubItemDebug.Visibility = DevExpress.XtraBars.BarItemVisibility.Always;
#endif
		}

		private void HandleRapidFireSetup() {
			if (AppContext.PersistentState.PwsEnabled) {
				StartRapidFire();
			}else {
				CancelRapidFire("Not enabled.");
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

			try {
				if (_dbStore.DeleteSensor(sensorInfo.Name))
					Log.InfoFormat("Sensor '{0}' was deleted by the user.", sensorInfo.Name);
				else
					Log.ErrorFormat("Sensor '{0}' deletion failed and was requested by the user.", sensorInfo.Name);
			}
			catch(Exception ex) {
				Log.Error("Error deleting sensor '" + sensorInfo.Name + "'.", ex);
			}

			ReloadHistoric();
		}

		private void OnRenameRequested(ISensorInfo sensorInfo) {
			if (null == sensorInfo || String.IsNullOrEmpty(sensorInfo.Name) || null == _dbStore) {
				MessageBox.Show("Invalid target", "Error");
				return;
			}

			var renameForm = new RenameForm() {
				Value = sensorInfo.Name,
				Text = "Rename " + sensorInfo.Name
			};
			var renameRes = renameForm.ShowDialog(this);
			if (renameRes != DialogResult.OK)
				return;
			if(renameForm.Value == sensorInfo.Name)
				return;

			try {
				if (_dbStore.RenameSensor(sensorInfo.Name, renameForm.Value))
					Log.InfoFormat("Sensor '{0}' renamed to '{1}' by user.", sensorInfo.Name, renameForm.Value);
				else
					MessageBox.Show("Rename failed.", "Error");
			}
			catch(Exception ex) {
				Log.Error("Error renaming sensor '" + sensorInfo.Name + "' to '" + renameForm.Value + "'.", ex);
			}

			ReloadHistoric();
		}

		private void timerLive_Tick(object sender, EventArgs e) {
			if(!backgroundWorkerLiveGraph.IsBusy)
				backgroundWorkerLiveGraph.RunWorkerAsync();
			//UpdateLiveGraph();
		}

		private void barButtonItemPrefs_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			try {
				var settingsForm = new SettingsForm(AppContext.PersistentState);
				settingsForm.ShowDialog(this);
			}
			catch(Exception ex) {
				Log.Error("Settings change failed.", ex);
				MessageBox.Show("Settings change failed.", "Failure");
			}
			HandleRapidFireSetup();
		}

		private void simpleButtonFindSensors_Click(object sender, EventArgs e) {
			FindSensors();
		}

		private void barButtonItemSensorSetup_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			FindSensors();
		}

		private void FindSensors() {
			var rapidFireEnabled = timerRapidFire.Enabled;
			try {
				if (rapidFireEnabled) {
					timerRapidFire.Enabled = false;
				}
				timerLive.Enabled = false;
				var findSensorForm = new FindSensorsDialog(_deviceConnection);
				findSensorForm.ShowDialog(this);
			}
			catch (Exception ex) {
				Log.Error("Failed to find sensors.", ex);
			}
			finally{
				timerLive.Enabled = true;
				if (rapidFireEnabled) {
					timerRapidFire.Enabled = true;
				}
			}
		}

		private void barButtonItemImport_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			DownloadDataDialog();
		}

		private void simpleButtonDownloadData_Click(object sender, EventArgs e) {
			DownloadDataDialog(true);
		}

		private void DownloadDataDialog(bool auto = false) {
			try {
				var importForm = new ImportDataForm(_dbStore, _deviceConnection){
					AutoImport = true,
					PersistentState = AppContext.PersistentState
				};
				importForm.ShowDialog(this);
			}
			catch(Exception ex) {
				Log.Error("Download data failed.", ex);
			}
			ReloadHistoric();
		}

		public void ReloadHistoric() {
			var historicSensors = _dbStore.GetAllSensorInfos();
			_historicSensorViewPanelController.UpdateView(historicSensors, AppContext.PersistentState);
			RequestHistoricalUpdate();
		}

		private void histNowChk_CheckedChanged(object sender, EventArgs e) {
			RequestHistoricalUpdate();
		}

		private void barButtonItemTimeCorrection_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			try {
				var timeCorrectionDialog = new TimeCorrection(_dbStore);
				timeCorrectionDialog.ShowDialog(this);
			}
			catch(Exception ex) {
				Log.Error("Time correction failed.", ex);
			}
			ReloadHistoric();
		}

		private void barButtonItemExport_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			try {
				var exportForm = new ExportForm(_dbStore);
				exportForm.ShowDialog(this);
			}
			catch(Exception ex) {
				Log.Error("Export failed.", ex);
			}
		}

		private void barButtonItemTimeSync_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			ShowTimeSyncDialog();
		}

		private void ShowTimeSyncDialog() {
			try {
				var timeSync = new TimeSync(_deviceConnection, _dbStore);
				timeSync.ShowDialog(this);
			}
			catch(Exception ex) {
				Log.Error("Time sync failed.", ex);
			}
		}

		private void barButtonItemExit_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			Close();
		}

		private void barButtonItemFirmwareUpdate_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			if(!(_deviceConnection is UsbDaqConnection)) {
				MessageBox.Show("Device is not supported.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
				return;
			}
			try {
				timerQueryTime.Stop();
				timerLive.Stop();
				var patchForm = new PatcherForm(_deviceConnection as UsbDaqConnection);
				patchForm.ShowDialog();
			}
			catch(Exception ex) {
				Log.Error("Firmware update failed.", ex);
			}
			finally {
				timerLive.Start();
				timerQueryTime.Start();
			}
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

		private readonly TimeSpan MaxClockUpdateFrequency = new TimeSpan(0,0,0,30);
		private DateTime LastClockUpdate = default(DateTime);

		private void timerQueryTime_Tick(object sender, EventArgs e) {

			UpdateClockStats();
			UpdateDaqStats();

		}

		private static string DisplayDateTime(DateTime v) {
			// dont use string format for date, let the culture handle that
			return v.ToShortDateString() + ' ' + v.ToString("HH:mm:ss");
		}

		private void UpdateClockStats() {
			var now = DateTime.Now;
			labelLocalTime.Text = DisplayDateTime(now);
			if (null != _deviceConnection) {
				var deviceTime = _deviceConnection.QueryClock();
				if (default(DateTime) != deviceTime) {
					labelDaqTime.Text = DisplayDateTime(deviceTime);
					var diff = deviceTime.Subtract(now);
					if (diff < TimeSpan.Zero) {
						diff = TimeSpan.Zero - diff;
					}

					bool outOfSync = diff >= new TimeSpan(0, 0, 0, 3, 0);
					labelDaqTime.ForeColor = ForeColor;

					if (outOfSync && AppContext.PersistentState.AutoSyncClock
						&& (now - LastClockUpdate) >= MaxClockUpdateFrequency
					) {
						System.Diagnostics.Debug.WriteLine("BeginClockSync");
						if (_deviceConnection.SetClock(DateTime.Now)) {
							labelDaqTime.ForeColor = ForeColor;
							System.Diagnostics.Debug.WriteLine("EndClockSync: OK!");
							LastClockUpdate = DateTime.Now;
							return;
						}
						System.Diagnostics.Debug.WriteLine("EndClockSync: Fail");
					}

					if (outOfSync) {
						labelDaqTime.ForeColor = now.Second % 2 == 0 ? Color.Red : ForeColor;
					}
					else {
						labelDaqTime.ForeColor = ForeColor;
					}
					return;
				}
			}
			labelDaqTime.Text = "N/A";
			labelDaqTime.ForeColor = ForeColor;
		}

		private void UpdateDaqStats() {
			const string na = "N/A";

			var temp = _deviceConnection.Temperature;
			var volBat = _deviceConnection.VoltageBattery;
			var volUsb = _deviceConnection.VoltageUsb;

			labelTmpDaq.Text = Double.IsNaN(temp) ? na : temp.ToString("F2");
			labelVolBat.Text = Double.IsNaN(volBat) ? na : volBat.ToString("F2");
			labelVolUsb.Text = Double.IsNaN(volUsb) ? na : volUsb.ToString("F2");
		}

		private void simpleButtonPwsAction_Click(object sender, EventArgs e) {
			if(timerRapidFire.Enabled) {
				CancelRapidFire("User canceled.");
			}
			else {
				AutoStartRapidFire();
			}
		}

		private void AutoStartRapidFire() {
			var stationNames = AppContext.PersistentState.StationNames;
			var stationPassword = AppContext.PersistentState.StationPassword;
			bool isValid = stationNames.Any(sn => !String.IsNullOrEmpty(sn))
				&& !String.IsNullOrEmpty(stationPassword);

			if (timerRapidFire.Enabled != isValid) {
				if(isValid) {
					StartRapidFire();
					timerRapidFire_Tick(null, null);
				}
			}
		}

		private const string RunningText = "Running";

		private void StartRapidFire() {
			timerRapidFire.Enabled = true;
			AppContext.PersistentState.PwsEnabled = true;

			//labelControlPwsStatus.Text = "Running";
			labelControlPwsStatus.SetPropertyThreadSafe(() => labelControlPwsStatus.Text, RunningText);
			//simpleButtonPwsAction.Text = "Running";
			simpleButtonPwsAction.SetPropertyThreadSafe(() => simpleButtonPwsAction.Text, RunningText + " (click to stop).");
			//simpleButtonPwsAction.BackColor = Color.LightGreen;
			simpleButtonPwsAction.SetPropertyThreadSafe(() => simpleButtonPwsAction.ForeColor, ForeColor);

		}

		private void CancelRapidFire(string message) {
			timerRapidFire.Enabled = false;
			AppContext.PersistentState.PwsEnabled = false;

			//labelControlPwsStatus.Text = message;
			labelControlPwsStatus.SetPropertyThreadSafe(() => labelControlPwsStatus.Text, message);
			//simpleButtonPwsAction.Text = "Disabled";
			simpleButtonPwsAction.SetPropertyThreadSafe(() => labelControlPwsStatus.Text, "Stopped (click to start).");
			//simpleButtonPwsAction.BackColor = Color.LightPink;
			simpleButtonPwsAction.SetPropertyThreadSafe(() => labelControlPwsStatus.ForeColor, Color.Red);
			Log.InfoFormat("PWS rapid fire canceled due to '{0}'.", message);
		}

		private void timerRapidFire_Tick(object sender, EventArgs e) {
			ISensor[] sensors = _deviceConnection.ToArray();

			IReading[] readings = sensors
				.Select(sensor => sensor.IsValid ? sensor.GetCurrentReading() : null)
				.ToArray()
			;

			if(readings.All(r => null == r)) {
				labelControlPwsStatus.Text = "No sensors.";
			}else {
				labelControlPwsStatus.Text = RunningText;
			}

			var stationNames = AppContext.PersistentState.StationNames;
			var stationPassword = AppContext.PersistentState.StationPassword;
			if (String.IsNullOrEmpty(stationPassword)) {
				CancelRapidFire("No PWS password.");
				return;
			}
			if(stationNames == null || stationNames.Count == 0) {
				CancelRapidFire("No PWS stations.");
				return;
			}
			for (int i = 0; i < readings.Length; i++) {
				string id = stationNames[i];
				if (String.IsNullOrEmpty(id)) {
					continue;
				}
				IReading reading = readings[i];
				if (null == reading || !reading.IsValid) {
					continue;
				}
				Dictionary<string, string> queryParams = new Dictionary<string, string>();
				queryParams.Add("action", "updateraw");
				queryParams.Add("ID", id);
				queryParams.Add("PASSWORD", stationPassword);
				DateTime utcStamp = reading.TimeStamp.ToUniversalTime();
				queryParams.Add("dateutc", utcStamp.ToString("yyyy-MM-dd hh:mm:ss"));
				queryParams.Add("realtime", "1");
				queryParams.Add("rtfreq", ((timerRapidFire.Interval / 1000.0).ToString()));
				if (reading.IsWindDirectionValid && reading.WindDirection >= 0 && reading.WindDirection <= 360.0) {
					queryParams.Add("winddir", ((int)(reading.WindDirection)).ToString());
				}
				if (reading.IsWindSpeedValid) {
					var speedConverter = ReadingValuesConverterCache<Reading>.SpeedCache
						.Get(sensors[i].SpeedUnit, SpeedUnit.MilesPerHour);
					var speed = speedConverter.Convert(reading.WindSpeed);
					queryParams.Add("windspeedmph",speed.ToString());
				}
				if (reading.IsHumidityValid) {
					queryParams.Add("humidity", (reading.Humidity * 100.0).ToString());
				}
				if (reading.IsTemperatureValid) {
					var tempConverter = ReadingValuesConverterCache<Reading>.TemperatureCache
						.Get(sensors[i].TemperatureUnit, TemperatureUnit.Fahrenheit);
					var temperature = tempConverter.Convert(reading.Temperature);
					queryParams.Add("tempf",temperature.ToString());
				}
				if (reading.IsPressureValid) {
					var pressConverter = ReadingValuesConverterCache<Reading>.PressCache
						.Get(sensors[i].PressureUnit, PressureUnit.InchOfMercury);
					var pressure = pressConverter.Convert(reading.Pressure);
					queryParams.Add("baromin",pressure.ToString());
				}
				if(reading.IsHumidityValid && reading.IsTemperatureValid) {
					var tempConverterCelcius = ReadingValuesConverterCache<Reading>.TemperatureCache
						.Get(sensors[i].TemperatureUnit, TemperatureUnit.Celsius);
					var tempConverterCToF = ReadingValuesConverterCache<Reading>.TemperatureCache
						.Get(TemperatureUnit.Celsius, TemperatureUnit.Fahrenheit);

					var tempC = tempConverterCelcius.Convert(reading.Temperature);
					var dewPointC = DewPointCalculator.DewPoint(tempC, reading.Humidity);
					var dewPointF = tempConverterCToF.Convert(dewPointC);
					queryParams.Add("dewptf", dewPointF.ToString());
				}
				queryParams.Add(
					"softwaretype",
					"Atmo"
				);

				var builder = new UriBuilder("http://rtupdate.wunderground.com/weatherstation/updateweatherstation.php");
				builder.Query = String.Join(
					"&",
					queryParams
						.Select(kvp => String.Concat(Uri.EscapeDataString(kvp.Key), '=', Uri.EscapeDataString(kvp.Value)))
						.ToArray()
				);
				try {
					var reqSent = HttpWebRequest.Create(builder.Uri);
					reqSent.BeginGetResponse(HandleRapidFireResult, reqSent);
				}
				catch(Exception ex) {
					Log.Warn("PWS rapid fire failure.", ex);
				}
			}

		}

		private void HandleRapidFireResult(IAsyncResult result) {
			var req = (WebRequest)result.AsyncState;
			try {
				using (WebResponse res = req.EndGetResponse(result)) {
					using (var reader = new StreamReader(res.GetResponseStream())) {
						string responseMessage = reader.ReadToEnd().ToUpperInvariant();
						if (responseMessage.StartsWith("SUCCESS")) {
							; // OK
						}
						else if (responseMessage.Contains("PASSWORD")) {
							CancelRapidFire("PWS Station ID or password is invalid.");
						}
						else {
							CancelRapidFire("PWS protocol error.");
						}
					}
				}
			}
			catch (WebException webEx) {
				CancelRapidFire("PWS Communication failure.");
				Log.Warn("PWS rapid fire was disabled due to an error.", webEx);
			}
		}

		/// <summary>
		/// Clean up any resources being used.
		/// </summary>
		/// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
		protected override void Dispose(bool disposing) {
			if (disposing) {
				if (components != null) {
					components.Dispose();
				}
				try {
					if (_deviceConnection is IDisposable) {
						(_deviceConnection as IDisposable).Dispose();
					}
				}
				catch (Exception ex) {
					Log.Warn("Shutdown problem: device connection.", ex);
				}

				try {
					if (_dbStore is IDisposable) {
						(_dbStore as IDisposable).Dispose();
					}
				}
				catch (Exception ex) {
					Log.Warn("Shutdown problem: database.", ex);
				}
			}
			base.Dispose(disposing);
		}

		private void simpleButtonTimeSync_Click(object sender, EventArgs e) {
			ShowTimeSyncDialog();
		}

		private void simpleButtonTempSource_Click(object sender, EventArgs e) {
			var newState = !_deviceConnection.UsingDaqTemp;
			_deviceConnection.UseDaqTemp(newState);
		}

		private void HandleDaqTemperatureSourceSet(bool isDaqTempSource) {
			string text;
			Image image;
			if (isDaqTempSource) {
				text = "Temperature Sensor: DAQ (click to change)";
				image = Properties.Resources.Temp_Sensor_01;
			}
			else {
				text = "Temperature Sensor: Anemometer (click to change)";
				image = Properties.Resources.Temp_Sensor_02;
			}
			simpleButtonTempSource.SetPropertyThreadSafe(() => simpleButtonTempSource.Text, text);
			simpleButtonTempSource.SetPropertyThreadSafe(() => simpleButtonTempSource.Image, image);
		}

		private void barButtonItemAbout_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			var aboutForm = new AboutForm();
			aboutForm.ShowDialog(this);
		}



		private void backgroundWorkerLiveGraph_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e) {
			UpdateLiveGraph();
		}

		public void UpdateLiveGraph() {

			HandleDaqTemperatureSourceSet(_deviceConnection.UsingDaqTemp);
			// current live state
			var now = DateTime.Now;
			var sensors = _deviceConnection.Where(s => s.IsValid).ToList();
			// get readings
			var readings = new Dictionary<ISensor, IReading>();
			foreach (var sensor in sensors) {
				var reading = sensor.GetCurrentReading();
				if (reading.IsValid) {
					readings.Add(sensor, reading);
				}
			}

			// save to memory
			foreach (var reading in readings) {
				if (!_memoryDataStore.GetAllSensorInfos().Any(si => si.Name.Equals(reading.Key.Name))) {
					_memoryDataStore.AddSensor(reading.Key);
				}
				_memoryDataStore.Push(reading.Key.Name, new[]{reading.Value});
			}

			// the current sensor views
			var sensorViews = _sensorViewPanelControler.Views.ToList();

			// determine which sensors are enabled
			var enabledSensors = new List<ISensor>();
			for (int i = 0; i < sensors.Count && i < sensorViews.Count; i++) {
				if (sensorViews[i].IsSelected) {
					enabledSensors.Add(sensors[i]);
				}
			}

			var liveDataTimeSpan = liveAtmosphericHeader.TimeRange.SelectedSpan;
			TimeSpan liveUpdateInterval;
			if(liveDataTimeSpan > new TimeSpan(1,0,0))
				liveUpdateInterval = new TimeSpan(0,1,0);
			else if(liveDataTimeSpan >= new TimeSpan(1,0,0))
				liveUpdateInterval = new TimeSpan(0,0,15);
			else
				liveUpdateInterval = new TimeSpan(0,0,0,0,500);

			var liveDataEnabled =
				(_lastLiveUpdateInterval != liveUpdateInterval)
				|| (now - liveUpdateInterval > _lastLiveUpdate);

			_lastLiveUpdateInterval = liveUpdateInterval;

			// pass it off to the live data graphs/tables
			if (liveDataEnabled) {
				_lastLiveUpdate = now;
				TimeUnit meanLiveUnit;
				if(liveDataTimeSpan >= new TimeSpan(1,0,0))
					meanLiveUnit = TimeUnit.Minute;
				else
					meanLiveUnit = TimeUnit.Second;

				// gather the data for each selected sensor
				var enabledSensorsLiveMeans = new List<List<ReadingAggregate>>(enabledSensors.Count);
				foreach (var sensor in enabledSensors) {

					// get the recent readings
					var recentReadings = _memoryDataStore.GetReadings(sensor.Name, now, TimeSpan.Zero.Subtract(liveDataTimeSpan));

					// calculate the mean data
					var means = StatsUtil.AggregateMean(recentReadings, meanLiveUnit).ToList();

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
				liveAtmosphericGraph.Invoke(new Action(() => {
					liveAtmosphericGraph.TemperatureUnit = TemperatureUnit;
					liveAtmosphericGraph.PressureUnit = PressureUnit;
					liveAtmosphericGraph.SpeedUnit = SpeedUnit;
					liveAtmosphericGraph.FormatTimeAxis(liveDataTimeSpan);
					liveAtmosphericGraph.SetLatest(enabledSensorsCompiledMeans.LastOrDefault());
					liveAtmosphericGraph.State = AppContext.PersistentState;
					liveAtmosphericGraph.SetDataSource(enabledSensorsCompiledMeans);
					// update the sensor controls
				}));

			}

			Invoke(new Action(() => { _sensorViewPanelControler.UpdateView(sensors, AppContext.PersistentState); }));
		}

		private void backgroundWorkerLiveGraph_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e) {
			//System.Diagnostics.Debug.WriteLine("Live Data End");
		}

		public void RequestHistoricalUpdate() {
			if(Monitor.TryEnter(_historicalUpdateThreadMutex)) {
				Monitor.Exit(_historicalUpdateThreadMutex);
				ThreadPool.QueueUserWorkItem(HistoricalDataUpdate);
			}
		}

		private void HistoricalDataUpdate(object junk) {
			if(!Visible) {
				lock (_historicalUpdateThreadMutex) {
					Thread.Sleep(100);
				}
				RequestHistoricalUpdate();
				return;
			}

			lock (_historicalUpdateThreadMutex) {
				System.Diagnostics.Debug.WriteLine("Begin Historical Data");

				var now = DateTime.Now;
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

				var historicalSelected =
					_dbStore.GetAllSensorInfos()
					.Where(si => _historicSensorViewPanelController.IsSensorSelected(si))
					.ToList();

				var sensorReadings = historicalSelected.Select(
					sensor =>
					_dbStore.GetReadingSummaries(
						sensor.Name, cumTimeInfo.MaxStamp,
						cumTimeInfo.MinStamp - cumTimeInfo.MaxStamp,
					    UnitUtility.ChooseBestSummaryUnit(histTimeSpan)
					)
				);

				var historicalSummaries = StatsUtil
					.JoinReadingSummaryEnumerable(sensorReadings)
					.ToList();

				if (!IsDisposed) {
					BeginInvoke(new Action(() => {
					    windResourceGraph.TemperatureUnit = TemperatureUnit;
					    windResourceGraph.PressureUnit = PressureUnit;
					    windResourceGraph.SpeedUnit = SpeedUnit;


					    windResourceGraph.SetDataSource(historicalSummaries);

					    historicalGraphBreakdown.TemperatureUnit = TemperatureUnit;
					    historicalGraphBreakdown.PressureUnit = PressureUnit;
					    historicalGraphBreakdown.SpeedUnit = SpeedUnit;
					    historicalGraphBreakdown.StepBack = !histNowChk.Checked;
					    historicalGraphBreakdown.DrillStartDate = histStartDate;
					    historicalGraphBreakdown.CumulativeTimeSpan = histTimeSpan;

					    historicalGraphBreakdown.SetDataSource(historicalSummaries);
					}));
				}

			}
		}

		private IEnumerable<PackedReading> GenerateFakeReadings(TimeSpan backSpan, DateTime timeFrom) {
			var oneSecond = new TimeSpan(0, 0, 0, 1);
			var timeTo = timeFrom - backSpan;
			var rand = new Random((int)DateTime.Now.Ticks);
			IReadingValues lastReading = null;
			for (var curTime = timeTo; curTime <= timeFrom; curTime += oneSecond) {
				var curReading = Demo.DemoDaqConnection.DemoSensor.GetCurrentReading(curTime, rand, lastReading);
				yield return curReading;
				lastReading = curReading;
			}
			yield break;
		}

		private void barButtonItemAdd24Hours_ItemClick(object sender, DevExpress.XtraBars.ItemClickEventArgs e) {
			DateTime now = DateTime.Now;
			const int maxSensors = 2;
			var sensors = _deviceConnection.Take(maxSensors).ToList();
			foreach (var sensor in sensors) {
				var span = new TimeSpan(0, 24, 0, 0);
				var readings = GenerateFakeReadings(span, now);
				_memoryDataStore.Push(sensor.Name, readings.Select(r => new Reading(r)));
				Log.DebugFormat("Added {0} of data to '{1}' ({2}).", span, sensor.Name, sensor);
			}
		}
	}

}
