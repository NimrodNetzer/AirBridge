package com.airbridge.app.transport.discovery

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import com.airbridge.app.core.models.DeviceInfo
import com.airbridge.app.core.models.DeviceType
import com.airbridge.app.transport.interfaces.IDiscoveryService
import com.airbridge.app.transport.protocol.ProtocolMessage
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.net.InetAddress
import java.util.concurrent.atomic.AtomicBoolean
import javax.inject.Inject
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

/**
 * mDNS-based peer discovery service using Android [NsdManager].
 *
 * Advertises this device as `_airbridge._tcp.` on [ProtocolMessage.DEFAULT_PORT]
 * and discovers peer devices on the local network. Discovered services are resolved
 * to [DeviceInfo] instances and emitted on [visibleDevicesFlow].
 *
 * **Lifecycle**: call [start] once to register + begin discovery; call [stop] to tear down.
 * Safe to call [start]/[stop] from any coroutine — internally dispatches to [Dispatchers.IO].
 */
class NsdDiscoveryService @Inject constructor(
    @ApplicationContext private val context: Context
) : IDiscoveryService {

    companion object {
        private const val SERVICE_TYPE = "_airbridge._tcp."
        private const val SERVICE_NAME = "AirBridge"
        private const val ATTR_DEVICE_ID   = "deviceId"
        private const val ATTR_DEVICE_NAME = "deviceName"
        private const val ATTR_DEVICE_TYPE = "deviceType"
    }

    private val nsdManager: NsdManager =
        context.getSystemService(Context.NSD_SERVICE) as NsdManager

    private val _visibleDevices = MutableStateFlow<List<DeviceInfo>>(emptyList())
    override val visibleDevicesFlow: Flow<List<DeviceInfo>> = _visibleDevices.asStateFlow()

    /** Guards mutations to [_visibleDevices] and the resolve queue. */
    private val devicesMutex = Mutex()

    /** Tracks discovered service names that are currently being resolved or are resolved. */
    private val resolving = AtomicBoolean(false)
    private val resolveQueue = ArrayDeque<NsdServiceInfo>()

    private var registrationListener: NsdManager.RegistrationListener? = null
    private var discoveryListener: NsdManager.DiscoveryListener? = null

    // -------------------------------------------------------------------------
    // IDiscoveryService
    // -------------------------------------------------------------------------

    override suspend fun start(): Unit = withContext(Dispatchers.IO) {
        registerService()
        startDiscovery()
    }

    override suspend fun stop(): Unit = withContext(Dispatchers.IO) {
        discoveryListener?.let {
            try { nsdManager.stopServiceDiscovery(it) } catch (_: Exception) {}
        }
        registrationListener?.let {
            try { nsdManager.unregisterService(it) } catch (_: Exception) {}
        }
        discoveryListener = null
        registrationListener = null
        devicesMutex.withLock { _visibleDevices.value = emptyList() }
    }

    override fun getVisibleDevices(): List<DeviceInfo> = _visibleDevices.value

    // -------------------------------------------------------------------------
    // Registration
    // -------------------------------------------------------------------------

    private suspend fun registerService() {
        val serviceInfo = NsdServiceInfo().apply {
            serviceName = SERVICE_NAME
            serviceType = SERVICE_TYPE
            port = ProtocolMessage.DEFAULT_PORT
        }

        suspendCancellableCoroutine { cont ->
            val listener = object : NsdManager.RegistrationListener {
                override fun onServiceRegistered(info: NsdServiceInfo) {
                    if (cont.isActive) cont.resume(Unit)
                }

                override fun onRegistrationFailed(info: NsdServiceInfo, errorCode: Int) {
                    if (cont.isActive) cont.resumeWithException(
                        RuntimeException("NSD registration failed: $errorCode")
                    )
                }

                override fun onServiceUnregistered(info: NsdServiceInfo) {}
                override fun onUnregistrationFailed(info: NsdServiceInfo, errorCode: Int) {}
            }
            registrationListener = listener
            nsdManager.registerService(serviceInfo, NsdManager.PROTOCOL_DNS_SD, listener)
        }
    }

    // -------------------------------------------------------------------------
    // Discovery
    // -------------------------------------------------------------------------

    private fun startDiscovery() {
        val listener = object : NsdManager.DiscoveryListener {
            override fun onStartDiscoveryFailed(serviceType: String, errorCode: Int) {
                // Retry discovery on failure — common NsdManager quirk
                nsdManager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, this)
            }

            override fun onStopDiscoveryFailed(serviceType: String, errorCode: Int) {}

            override fun onDiscoveryStarted(serviceType: String) {}

            override fun onDiscoveryStopped(serviceType: String) {}

            override fun onServiceFound(serviceInfo: NsdServiceInfo) {
                if (serviceInfo.serviceType != SERVICE_TYPE) return
                enqueueResolve(serviceInfo)
            }

            override fun onServiceLost(serviceInfo: NsdServiceInfo) {
                removeDevice(serviceInfo.serviceName)
            }
        }
        discoveryListener = listener
        nsdManager.discoverServices(SERVICE_TYPE, NsdManager.PROTOCOL_DNS_SD, listener)
    }

    // -------------------------------------------------------------------------
    // Resolve queue — NsdManager only supports one concurrent resolve
    // -------------------------------------------------------------------------

    private fun enqueueResolve(serviceInfo: NsdServiceInfo) {
        resolveQueue.addLast(serviceInfo)
        drainResolveQueue()
    }

    private fun drainResolveQueue() {
        if (resolving.compareAndSet(false, true)) {
            val next = resolveQueue.removeFirstOrNull()
            if (next == null) {
                resolving.set(false)
                return
            }
            resolveOne(next)
        }
    }

    private fun resolveOne(serviceInfo: NsdServiceInfo) {
        nsdManager.resolveService(serviceInfo, object : NsdManager.ResolveListener {
            override fun onResolveFailed(info: NsdServiceInfo, errorCode: Int) {
                // Re-queue on failure (common with concurrent resolves)
                resolveQueue.addLast(info)
                resolving.set(false)
                drainResolveQueue()
            }

            override fun onServiceResolved(info: NsdServiceInfo) {
                addOrUpdateDevice(info)
                resolving.set(false)
                drainResolveQueue()
            }
        })
    }

    // -------------------------------------------------------------------------
    // Device list mutations
    // -------------------------------------------------------------------------

    private fun addOrUpdateDevice(info: NsdServiceInfo) {
        val device = buildDeviceInfo(info) ?: return
        val current = _visibleDevices.value.toMutableList()
        val idx = current.indexOfFirst { it.deviceId == device.deviceId }
        if (idx >= 0) current[idx] = device else current.add(device)
        _visibleDevices.value = current.toList()
    }

    private fun removeDevice(serviceName: String) {
        _visibleDevices.value = _visibleDevices.value.filter { it.deviceName != serviceName }
    }

    /**
     * Builds a [DeviceInfo] from a resolved [NsdServiceInfo].
     *
     * The service host name is used as a stable device ID when no explicit TXT attribute
     * is present. Device type defaults to [DeviceType.WINDOWS_PC] (primary peer type).
     */
    private fun buildDeviceInfo(info: NsdServiceInfo): DeviceInfo? {
        val host: InetAddress = info.host ?: return null
        val ipAddress = host.hostAddress ?: return null

        val attrs = info.attributes  // Map<String, ByteArray> on API 21+

        val deviceId = attrs[ATTR_DEVICE_ID]?.toString(Charsets.UTF_8)
            ?: info.serviceName

        val deviceName = attrs[ATTR_DEVICE_NAME]?.toString(Charsets.UTF_8)
            ?: info.serviceName

        val deviceType = attrs[ATTR_DEVICE_TYPE]?.toString(Charsets.UTF_8)
            ?.let { runCatching { DeviceType.valueOf(it) }.getOrNull() }
            ?: DeviceType.WINDOWS_PC

        return DeviceInfo(
            deviceId    = deviceId,
            deviceName  = deviceName,
            deviceType  = deviceType,
            ipAddress   = ipAddress,
            port        = info.port,
            isPaired    = false
        )
    }
}
