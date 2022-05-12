/*
Copyright NetFoundry Inc.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

https://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OpenZiti {
	public class ZitiIdentity {
		private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

		private SemaphoreSlim runlock = new SemaphoreSlim(0);
		private SemaphoreSlim streamLock = new SemaphoreSlim(0);
		private bool _isRunning;
		private object _isRunningLock = new object();
		private Exception startException;
		private bool isInitialized;
		private Dictionary<string, ZitiService> services = new Dictionary<string, ZitiService>();
		private object _configFileLock = new object();

		public bool IsRunning {
			get {
				lock (_isRunningLock) {
					return _isRunning;
				}
			}
			internal set {
				lock (_isRunningLock) {
					_isRunning = value;
				}
			}
		}

		private UVLoop _loop = API.DefaultLoop; //use the default loop unless changed
		private object _loopLock = new object();

		public UVLoop Loop {
			get {
				lock (_loopLock) {
					return _loop;
				}
			}
			set {
				if (IsRunning) {
					throw new System.InvalidOperationException("the loop cannot be changed once it is running");
				} else {
					lock (_loopLock) {
						_loop = value;
					}
				}
			}
		}

		public ZitiStatus InitStats { get; internal set; }
		public string InitStatusError { get; internal set; }
		public string IdentityNameFromController { get; internal set; }
		public string ControllerVersion { get; internal set; }
		public bool ControllerConnected { get; internal set; }
		public object ApplicationContext { get; internal set; }
		public InitOptions InitOpts { get; internal set; }
		internal IntPtr NativeContext;
		internal ZitiContext WrappedContext { get; set; }

		public string ConfigFilePath { get; private set; }

		private Native.IdentityFile nid;
		private UVLoop uvLoop;
		private const int DefaultRefreshInterval = 15;

		public string ControllerURL => nid.ztAPI;

		public ZitiIdentity(InitOptions opts) {
			this.InitOpts = opts;
			if (opts.IdentityFile != null) {
				string json = File.ReadAllText(opts.IdentityFile);
				nid = JsonSerializer.Deserialize<Native.IdentityFile>(json);
			}
		}

		public ZitiService GetService(string serviceName) {
			//ZitiService svc = new ZitiService(null, IntPtr.Zero);;
			throw new NotImplementedException();
		}

		public ZitiConnection NewConnection(string serviceName) {
			if (!isInitialized) {
				throw new ZitiException("This identity is not yet initialized. InitializeAndRun must be called before creating a connection.");
			}
			if (ServiceAvailable(serviceName)) {
				var found = services.FirstOrDefault(kvp => kvp.Value.Name == serviceName);
				if (found.Value == null) {
					throw new KeyNotFoundException("The service named " + serviceName + " was not found");
				}

				return new ZitiConnection(found.Value, WrappedContext, serviceName);
			}
			throw new KeyNotFoundException("The service named " + serviceName + " was not found");
		}

		/// <summary>
		/// Determines if the provided serviceName is available for this identity
		/// </summary>
		/// <param name="serviceName">The service name to verify</param>
		/// <returns>If the service exists - true, false if not</returns>
		public bool ServiceAvailable(string serviceName) {
			IntPtr ctx = default;
			int result = Native.API.ziti_service_available(NativeContext, serviceName, ziti_service_cb, ctx);
			if (result == 0) {
				return true;
			} else if (result == (int)ZitiStatus.SERVICE_UNAVAILABLE) {
				return false;
			}
			throw new ZitiException(((ZitiStatus)result).GetDescription());
		}

		private void ziti_service_cb(IntPtr ziti_context, IntPtr ziti_service, int status, GCHandle on_service_context) {
			//throw new NotImplementedException();
			Logger.Debug("here we are");
		}

		public void Run() {
			RunAsync(DefaultRefreshInterval).Wait();
		}

		public void Run(int refreshInterval) {
			RunAsync(refreshInterval).Wait();
		}

		public async Task RunAsync() {
			await RunAsync(DefaultRefreshInterval).ConfigureAwait(false);
		}

		public async Task RunAsync(int refreshInterval) {
			ZitiOptions zitiOptions = Configure(refreshInterval); //use default refresh interval if not supplied

			if (this.IsRunning) {
				throw new System.InvalidOperationException("The identity is already running");
			}

			new Thread(() => Native.API.z4d_uv_run(Loop.nativeUvLoop)).Start();
			await runlock.WaitAsync().ConfigureAwait(false);
			GC.KeepAlive(zitiOptions);
		}

		public ZitiOptions Configure(int refreshInterval) {
			Native.API.ziti_log_init(Loop.nativeUvLoop, 11, Marshal.GetFunctionPointerForDelegate(API.NativeLogger));
			IntPtr cfgs = Native.NativeHelperFunctions.ToPtr(InitOpts.ConfigurationTypes);
			// fix value assignment of refreshInterval

			Native.ziti_options ziti_opts = new Native.ziti_options {
				//app_ctx = GCHandle.Alloc(InitOpts.ApplicationContext, GCHandleType.Pinned),
				config = InitOpts.IdentityFile,
				config_types = cfgs,
				refresh_interval = 15,
				metrics_type = InitOpts.MetricType,
				pq_mac_cb = native_ziti_pq_mac_cb,
				events = InitOpts.EventFlags,
				event_cb = ziti_event_cb,
			};
			ApplicationContext = InitOpts.ApplicationContext;

			InitOpts.OnZitiContextEvent += SaveNativeContext;
			StructWrapper ziti_opts_ptr = new StructWrapper(ziti_opts);

			int result = Native.API.ziti_init_opts(ziti_opts_ptr.Ptr, Loop.nativeUvLoop);
			if (result != 0) {
				throw new ZitiException("Could not initialize the connection using the given ziti options.");
			}
			return new ZitiOptions() {
				NativeZitiOptions = ziti_opts
			};
		}

		public void Shutdown() {
			runlock.Release();
			try {
				Native.API.ziti_shutdown(this.NativeContext);

				Logger.Info("Shutdown complete");
			}
			catch (Exception e) {
				Logger.Debug("Shutdown complete with error: " + e.Message);
			}
		}

		private void ziti_event_cb(IntPtr ziti_context, IntPtr ziti_event_t) {
			int type = Native.NativeHelperFunctions.ziti_event_type_from_pointer(ziti_event_t);
			switch (type) {
				case ZitiEventFlags.ZitiContextEvent:
					NativeContext = ziti_context;
					WrappedContext = new ZitiContext(ziti_context);
					Native.ziti_context_event ziti_context_event;
					if (OpenZiti.GlobalConstants.X64)
					{
						ziti_context_event = Marshal.PtrToStructure<Native.ziti_context_event_x64>(ziti_event_t);
					}
					else
					{
						ziti_context_event = Marshal.PtrToStructure<Native.ziti_context_event_x86>(ziti_event_t);
					}

					var vptr = Native.API.ziti_get_controller_version(ziti_context);
					ziti_version v = Marshal.PtrToStructure<ziti_version>(vptr);
					IntPtr ptr = Native.API.ziti_get_controller(ziti_context);
					string ztapi = marshallPointerToString(ptr);
					var idPtr = Native.API.ziti_get_identity(ziti_context);
					string name = null;
					if (idPtr != IntPtr.Zero)
					{
						ziti_identity zid = Marshal.PtrToStructure<ziti_identity>(idPtr);
						name = zid.name;
						this.IdentityNameFromController = zid.name;
					}

					ZitiContextEvent evt = new ZitiContextEvent() {
						Name = name,
						ZTAPI = ztapi,
						Status = (ZitiStatus)ziti_context_event.GetCtrlStatus(),
						Version = v,
						Identity = this,
					};
					if (ziti_context_event.GetErr() != IntPtr.Zero)
					{
						evt.StatusError = marshallPointerToString(ziti_context_event.GetErr());
					}

					InitOpts.ZitiContextEvent(evt);

					lock (this) {
						Monitor.Pulse(this);
						isInitialized = true;
					}

					break;
				case ZitiEventFlags.ZitiRouterEvent:
					Native.ziti_router_event ziti_router_event;
					if (OpenZiti.GlobalConstants.X64)
					{
						ziti_router_event = Marshal.PtrToStructure<Native.ziti_router_event_x64>(ziti_event_t);
					}
					else
					{
						ziti_router_event = Marshal.PtrToStructure<Native.ziti_router_event_x86>(ziti_event_t);
					}

					ZitiRouterEvent routerEvent = new ZitiRouterEvent() {
						Name = Marshal.PtrToStringUTF8(ziti_router_event.GetName()),
						Type = (RouterEventType) ziti_router_event.GetStatus(),
						Version = Marshal.PtrToStringUTF8(ziti_router_event.GetVersion()),
					};
					InitOpts.ZitiRouterEvent(routerEvent);
					break;
				case ZitiEventFlags.ZitiServiceEvent:
					Native.ziti_service_event ziti_service_event;
					if (OpenZiti.GlobalConstants.X64)
					{
						ziti_service_event = Marshal.PtrToStructure<Native.ziti_service_event_x64>(ziti_event_t);
					}
					else
					{
						ziti_service_event = Marshal.PtrToStructure<Native.ziti_service_event_x86>(ziti_event_t);
					}

					ZitiServiceEvent serviceEvent = new ZitiServiceEvent(ziti_context) {
						nativeRemovedList = ziti_service_event.GetRemoved(),
						nativeChangedList = ziti_service_event.GetChanged(),
						nativeAddedList = ziti_service_event.GetAdded(),
						Context = this.ApplicationContext,
						id = this,
					};

					if (isStreaming) {
						foreach (var removed in serviceEvent.Removed()) {
							services.Remove(removed.Id);
						}
						foreach (var changed in serviceEvent.Changed()) {
							services[changed.Id] = changed;
						}
						foreach (var added in serviceEvent.Added()) {
							services[added.Id] = added;
						}

						streamLock.Release();
					}

					InitOpts.ZitiServiceEvent(serviceEvent);

					break;
				case ZitiEventFlags.ZitiMfaAuthEvent:
					Native.ziti_mfa_event ziti_mfa_event = Marshal.PtrToStructure<Native.ziti_mfa_event>(ziti_event_t);

					ZitiMFAEvent zitiMFAEvent = new ZitiMFAEvent()
					{
						id = this
					};

					InitOpts.ZitiMFAEvent(zitiMFAEvent);
					break;
				case ZitiEventFlags.ZitiAPIEvent:
					Native.ziti_api_event ziti_api_event;
					if (OpenZiti.GlobalConstants.X64)
					{
						ziti_api_event = Marshal.PtrToStructure<Native.ziti_api_event_x64>(ziti_event_t);
					}
					else
					{
						ziti_api_event = Marshal.PtrToStructure<Native.ziti_api_event_x86>(ziti_event_t);
					}

					if (ziti_api_event.GetNewCtrlAddress() == IntPtr.Zero)
					{
						Logger.Info("Ziti identifier received incorrect API event with null controller address");
						break;
					}

					ZitiAPIEvent zitiAPIEvent = new ZitiAPIEvent()
					{
						id = this,
						newCtrlAddress = Marshal.PtrToStringUTF8(ziti_api_event.GetNewCtrlAddress()),
					};

					Task.Run(() => {
						UpdateControllerUrlInConfigFile(zitiAPIEvent.newCtrlAddress);
					});

					InitOpts.ZitiAPIEvent(zitiAPIEvent);
					Logger.Info("Ziti identifier received API event with controller address {0}", zitiAPIEvent.newCtrlAddress);
					break;
				default:
					Logger.Warn("UNEXPECTED ZitiEventFlags [{0}]! Please report.", type);
					break;
			}
		}

		private void SaveNativeContext(object sender, ZitiContextEvent e) {
			Logger.Error("it's ");
			//this.NativeContext = e.na
		}

		public struct InitOptions {
			public object ApplicationContext;
			public string IdentityFile;

			public string[] ConfigurationTypes;

			//public int RefreshInterval;
			public RateType MetricType;
			public uint EventFlags;
			public event EventHandler<ZitiContextEvent> OnZitiContextEvent;
			public event EventHandler<ZitiRouterEvent> OnZitiRouterEvent;
			public event EventHandler<ZitiServiceEvent> OnZitiServiceEvent;
			public event EventHandler<ZitiMFAEvent> OnZitiMFAEvent;
			public event EventHandler<ZitiAPIEvent> OnZitiAPIEvent;
			public event EventHandler<ZitiMFAStatusEvent> OnZitiMFAStatusEvent;

			internal void ZitiContextEvent(ZitiContextEvent evt) {
				OnZitiContextEvent?.Invoke(this, evt);
			}

			internal void ZitiRouterEvent(ZitiRouterEvent evt) {
				OnZitiRouterEvent?.Invoke(this, evt);
			}

			internal void ZitiServiceEvent(ZitiServiceEvent evt) {
				OnZitiServiceEvent?.Invoke(this, evt);
			}

			internal void ZitiMFAEvent(ZitiMFAEvent evt)
			{
				OnZitiMFAEvent?.Invoke(this, evt);
			}

			internal void ZitiAPIEvent(ZitiAPIEvent evt)
			{
				OnZitiAPIEvent?.Invoke(this, evt);
			}

			internal void ZitiMFAStatusEvent(ZitiMFAStatusEvent evt)
			{
				OnZitiMFAStatusEvent?.Invoke(this, evt);
			}
		}
		public struct MFAStatusCB
		{
			public ZitiIdentity.InitOptions zidOpts;
			public delegate void ZitiResponseDelegate(object evt);

			public void ZitiResponse(object evt)
			{
				if (evt is ZitiMFAStatusEvent)
				{
					zidOpts.ZitiMFAStatusEvent((ZitiMFAStatusEvent)evt);
				}
			}
		}

		public void native_ziti_pq_mac_cb(IntPtr ziti_context, string id, Native.ziti_pr_mac_cb response_cb) {
			Logger.Debug("posture query cb...");
		}

		private void native_ziti_pr_mac_cb(IntPtr ziti_context, string id, string[] mac_addresses, int num_mac) {

		}


		private void timer(IntPtr handle) {
			//only exists to keep the UVLoop alive.
			Logger.Debug("loop is running...");
		}

		private IntPtr uvTimer;
		private DateTime start;
		private bool isStreaming;

		/// <summary>
		/// Initializes this identity with the NetFoundry network
		/// </summary>
		/// <exception cref="Exception">Thrown when the path to the configuration file no longer exists or if the provided identity file is not valid</exception>
		public void InitializeAndRun() {
			isStreaming = true;
			Task.Run(() => {
				try {
					start = DateTime.Now;
					uvLoop = API.DefaultLoop;
					long interval = 1000; //ms
					uvTimer = Native.API.z4d_registerUVTimer(uvLoop.nativeUvLoop, timer, interval, interval);
					this.Run();
				} catch (Exception e) {
					startException = e;

					lock (this) {
						Monitor.Pulse(this);
					}
				}
			});

			lock (this) {
				Monitor.Wait(this); //lock will be released in the AfterInitialize callback
				if (!isInitialized) {
					if (startException != null) {
						throw startException;
					}
				}
			}
		}

		public void Stop() {
			Native.API.z4d_stop_uv_timer(uvTimer);
		}

		public void WaitSync() {

		}

		public async Task WaitForServices() {
			await streamLock.WaitAsync().ConfigureAwait(false);
		}

		public void UpdateControllerUrlInConfigFile(string controller_url) {
			lock (_configFileLock) {
				string bkpConfigFileName = this.InitOpts.IdentityFile + ".bak";
				File.Move(this.InitOpts.IdentityFile, bkpConfigFileName);
				Logger.Debug("Created backup config file {0}", bkpConfigFileName);
				nid.ztAPI = controller_url;
				String json = JsonSerializer.Serialize<Native.IdentityFile>(nid);
				File.WriteAllText(this.InitOpts.IdentityFile, json);
				Logger.Debug("Created new config file {0}", this.InitOpts.IdentityFile);
			}
		}

		public void SetEnabled(bool enabled) {
			OpenZiti.Native.API.ziti_set_enabled(this.WrappedContext.nativeZitiContext, enabled);
		}

		public bool IsEnabled() {
			return OpenZiti.Native.API.ziti_is_enabled(this.WrappedContext.nativeZitiContext);
		}
		public void SubmitMFA(string code) {
			OpenZiti.Native.API.ziti_mfa_auth(this.WrappedContext.nativeZitiContext, code, MFA.AfterMFASubmit, MFA.GetMFAStatusDelegate(this));
		}

		public void EnrollMFA() {
			OpenZiti.Native.API.ziti_mfa_enroll(this.WrappedContext.nativeZitiContext, MFA.AfterMFAEnroll, MFA.GetMFAStatusDelegate(this));
		}

		public void VerifyMFA(string code) {
			OpenZiti.Native.API.ziti_mfa_verify(this.WrappedContext.nativeZitiContext, code, MFA.AfterMFAVerify, MFA.GetMFAStatusDelegate(this));
		}

		public void RemoveMFA(string code) {
			OpenZiti.Native.API.ziti_mfa_remove(this.WrappedContext.nativeZitiContext, code, MFA.AfterMFARemove, MFA.GetMFAStatusDelegate(this));
		}

		public void GetMFARecoveryCodes(string code) {
			OpenZiti.Native.API.ziti_mfa_get_recovery_codes(this.WrappedContext.nativeZitiContext, code, MFA.AfterMFARecoveryCodes, MFA.GetMFAStatusDelegate(this));
		}

		public void GenerateMFARecoveryCodes(string code) {
			OpenZiti.Native.API.ziti_mfa_new_recovery_codes(this.WrappedContext.nativeZitiContext, code, MFA.AfterMFARecoveryCodes, MFA.GetMFAStatusDelegate(this));
		}

		public TransferMetrics GetTransferRates() {
			PrimitiveWrapper<double> upIntPtrWrapper = new PrimitiveWrapper<double>();
			PrimitiveWrapper<double> downIntPtrWrapper = new PrimitiveWrapper<double>();
			OpenZiti.Native.API.ziti_get_transfer_rates(this.WrappedContext.nativeZitiContext, upIntPtrWrapper.Ptr, downIntPtrWrapper.Ptr);
			double[] upMetrics = new double[1];
			Marshal.Copy(upIntPtrWrapper.Ptr, upMetrics, 0, 1);
			double[] downMetrics = new double[1];
			Marshal.Copy(downIntPtrWrapper.Ptr, downMetrics, 0, 1);
			TransferMetrics tm = new TransferMetrics();
			tm.Up = upMetrics[0];
			tm.Down = downMetrics[0];
			return tm;
		}
		public void EndpointStateChange(bool woken, bool unlocked) {
			OpenZiti.Native.API.ziti_endpoint_state_change(this.WrappedContext.nativeZitiContext, woken, unlocked);
		}

		public void ZitiDumpToLog() {
			OpenZiti.Native.NativeHelperFunctions.z4d_ziti_dump_log(this.WrappedContext.nativeZitiContext);
		}
		public void ZitiDumpToFile(string fileName) {
			OpenZiti.Native.NativeHelperFunctions.z4d_ziti_dump_file(this.WrappedContext.nativeZitiContext, fileName);
		}

		private string marshallPointerToString(IntPtr ptr)
		{
			string response;
			if (OpenZiti.GlobalConstants.X64)
			{
				response = Marshal.PtrToStringAuto(ptr);
			}
			else
			{
				response = Marshal.PtrToStringUTF8(ptr);
			}
			return response;
		}
	}
	public struct TransferMetrics {
		public double Up;
		public double Down;
	}

	public class ZitiContextEvent {
		public ZitiStatus Status;
		public string StatusError;
		public string Name;
		public string ZTAPI;
		public ziti_version Version;
		public ZitiIdentity Identity;
	}

	public class ZitiRouterEvent {
		public string Name;
		public RouterEventType Type;
		public string Version;
	}

	public class ZitiServiceEvent {

		public ZitiServiceEvent(IntPtr zitiCtx) {
			this.ziti_ctx = zitiCtx;
		}
		internal IntPtr nativeRemovedList {
			set => removedList = createZitiServiceList(value);	
        }
		internal IntPtr nativeChangedList {
			set => changedList = createZitiServiceList(value);
        }
		internal IntPtr nativeAddedList {
			set => addedList = createZitiServiceList(value);
		}
		internal IntPtr ziti_ctx { get; }
		internal ZitiIdentity id;
		internal List<ZitiService> removedList;
		internal List<ZitiService> changedList;
		internal List<ZitiService> addedList;

		public object Context { get; internal set; }

		private IEnumerable<IntPtr> array_iterator(IntPtr arr) {
			int index = 0;
			while (true) {
				IntPtr zitiService = Native.API.ziti_service_array_get(arr, index);
				index++;
				if (zitiService == IntPtr.Zero) {
					break;
				}

				yield return zitiService;
			}
		}

		private List<ZitiService> createZitiServiceList(IntPtr nativeServiceArray) {
			List<ZitiService> servicesList = new List<ZitiService>();
			foreach (IntPtr p in array_iterator(nativeServiceArray)) {
				ZitiService svc = new ZitiService(id, new ZitiContext(ziti_ctx), p);
				servicesList.Add(svc);
			}
			return servicesList;
		}

		public IEnumerable<ZitiService> Removed() {
			foreach (ZitiService svc in removedList) {
				yield return svc;
			}
		}

		public IEnumerable<ZitiService> Changed() {
			foreach (ZitiService svc in changedList) {
				yield return svc;
			}
		}

		public IEnumerable<ZitiService> Added() {
			foreach (ZitiService svc in addedList) {
				yield return svc;
			}
		}
	}

	public class ZitiMFAEvent
	{
		public ZitiIdentity id;
	}

	public class ZitiAPIEvent
	{
		public ZitiIdentity id;
		public string newCtrlAddress;
	}

	public class ZitiMFAStatusEvent
	{
		public ZitiIdentity id;
		public ZitiStatus status;
		public bool isVerified;
		public MFAOperationType operationType;
		public string provisioningUrl;
		public string[] recoveryCodes;
	}

	public static class ZitiEventFlags {
		public const int All = -1;
		public const int ZitiContextEvent = 1;
		public const int ZitiRouterEvent = 1 << 1;
		public const int ZitiServiceEvent = 1 << 2;
		public const int ZitiMfaAuthEvent = 1 << 3;
		public const int ZitiAPIEvent = 1 << 4;
	}

	public struct ZitiOptions {
		internal Native.ziti_options NativeZitiOptions;
    }
}
