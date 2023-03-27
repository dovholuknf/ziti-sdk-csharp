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

using NLog;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace OpenZiti.legacy {
    /// <summary>
    /// Represents the connection to the Ziti Network.
    /// </summary>
    public class ZitiConnection {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Any additional context that needs to be stored along with the <see cref="ZitiConnection"/>. Must be
        /// supplied when the <see cref="ZitiConnection"/> is created.
        /// </summary>
        public object ConnectionContext {
            get {
                return nativeConnContext.Target;
            }
            private set {
                _connectionContext = value;
                nativeConnContext = GCHandle.Alloc(_connectionContext);
            }
        }

        internal byte[] NO_DATA = new byte[0];
        internal IntPtr nativeConnection = IntPtr.Zero;
        internal bool readyForWriting;
        internal bool readyForReading;
        internal BlockingCollection<byte[]> responses = new BlockingCollection<byte[]>(16);

        public ZitiService Service { get; internal set; }
        private readonly ZitiContext zitiContext = null;
        private object _connectionContext;
        private GCHandle nativeConnContext = ZitiUtil.NO_CONTEXT;
        private OnZitiDataWritten aafterData;
        private OnClientAccept onAccept;
        private OnZitiClientData onClientData;
        private bool isStream;
        private bool isDialed;
        private ZitiStatus connectionReadyStatus;
        private Native.ziti_conn_cb ozc;
        private Native.ziti_data_cb ozdr;

        /// <summary>
        /// The only constructor for <see cref="ZitiConnection"/>. A valid <see cref="ZitiService"/> and
        /// <see cref="ZitiContext"/> must be provided.
        /// </summary>
        /// <param name="service">The <see cref="ZitiService"/> to construct a <see cref="ZitiConnection"/> with</param>
        /// <param name="context"></param>
        /// <param name="connectionContext">Additional context that needs to be stored along with the <see cref="ZitiConnection"/></param>
        public ZitiConnection(ZitiService service, ZitiContext context, object connectionContext) {
            Service = service;
            ConnectionContext = connectionContext;

            //make initialze a connection in native code
            Native.API.ziti_conn_init(context.nativeZitiContext, out nativeConnection, GCHandle.ToIntPtr(nativeConnContext));
            zitiContext = context;
        }

        ~ZitiConnection() {
            if (_connectionContext != null) {
                nativeConnContext.SafeFreeGCHandle();
            }
        }

        public void Dial(OnZitiConnected onConnected, OnZitiDataReceived dataReceived) {
            var de = new DialEncapsulation {
                conn = this,
                onConnected = onConnected,
                dataReceived = dataReceived
            };
            de.dial(Service.Name);
            isDialed = true;
        }

        public int Write(byte[] data, OnZitiDataWritten afterDataWritten, object context) {
            return Write(data, data.Length, afterDataWritten, context);
        }

        public int Write(byte[] data, int len, OnZitiDataWritten afterDataWritten, object context) {
            aafterData = afterDataWritten;
            return Native.API.ziti_write(nativeConnection, data, len, afterData, GCHandle.ToIntPtr(GCHandle.Alloc(context)));
        }

        public int Accept(OnClientAccept onAccept, OnZitiClientData onClientData) {
            this.onAccept = onAccept;
            this.onClientData = onClientData;
            return Native.API.ziti_accept(nativeConnection, native_on_accept, native_on_client_data);
        }

        public void Close() {
            Native.API.z4d_ziti_close(nativeConnection);
        }

        private void native_on_accept(IntPtr ziti_connection, int status) {
            onAccept(this, (ZitiStatus)status);
        }

        private int native_on_client_data(IntPtr conn, IntPtr data, int len) {
            if (len > 0) {
                var bytes = new byte[len];
                Marshal.Copy(data, bytes, 0, len);
                onClientData(this, bytes, len, ZitiStatus.OK);
            } else {
                onClientData(this, NO_DATA, 0, ZitiStatus.OK);
            }
            return len;
        }

        private void afterData(IntPtr ziti_connection, int status, GCHandle write_context) {
            if (aafterData == null) {
                throw new Exception("aafter data is not set?");
            }
            if (status < 0) {
                aafterData(this, (ZitiStatus)status, write_context.Target);
            } else {
                aafterData(this, ZitiStatus.OK, write_context.Target);
            }
            write_context.SafeFreeGCHandle();
        }

        internal class DialEncapsulation {
            internal byte[] NO_DATA = new byte[0];

            internal ZitiConnection conn;
            internal OnZitiConnected onConnected;
            internal OnZitiDataReceived dataReceived;
            internal void dial(string serviceName) {
                Native.API.ziti_dial(conn.nativeConnection, serviceName, conn_cb, data_cb);
            }

            private void conn_cb(IntPtr ziti_connection, int status) {
                onConnected(conn, (ZitiStatus)status);
            }

            private int data_cb(IntPtr conn, IntPtr rawData, int len) {
                if (len < 0) {
                    dataReceived(this.conn, (ZitiStatus)len, NO_DATA);
                } else {
                    var data = new byte[len];
                    Marshal.Copy(rawData, data, 0, data.Length);
                    dataReceived(this.conn, ZitiStatus.OK, data);
                }
                return len;
            }
        }

        public void MarkAsStream() {
            isStream = true;
            if (isDialed) {
                throw new InvalidOperationException("Cannot AsStream on a connection that is already Dialed.");
            }

            //assign delegate to a local variable so that it is not elligable for GC
            ozc = (IntPtr nf_connection, int status) => {
                lock (this) {
                    if (status < 0) {
                        Logger.Debug("connection not ready for writing: " + status);
                        connectionReadyStatus = (ZitiStatus)status;
                    } else {
                        Logger.Debug("marking stream ready for writing");
                    }

                    Monitor.Pulse(this);
                    readyForWriting = true;
                }
            };

            //assign delegate to a local variable so that it is not elligable for GC
            ozdr = (IntPtr nf_connection, IntPtr rawData, int len) => {
                if (len > 0) {
                    //need to copy the memory from c - into managed code and then write into the pipeline buffer
                    //it would be much better to just read from 
                    var data = new byte[len];
                    Logger.Debug("got bytes from ziti: " + len + " responses size: " + responses.Count);
                    Marshal.Copy(rawData, data, 0, len);
                    responses.Add(data);

                    return len;
                } else {
                    responses.CompleteAdding();

                    return 0;
                }
            };
            //Ziti.Dial(nf_connection, serviceName, ozc, ozdr);
            Native.API.ziti_dial(nativeConnection, Service.Name, ozc, ozdr);
        }

        public bool CheckConnection() {
            if (connectionReadyStatus != ZitiStatus.OK) {
                throw new ZitiException("Connection is not in a usable state: " + connectionReadyStatus.GetDescription());
            }
            return true;
        }
    }
}
