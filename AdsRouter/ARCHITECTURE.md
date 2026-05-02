# AdsRouter Architektur

Ziel: TC1000-Funktionalitaet als eigene App. AMS-Schicht oben, Transport-Schicht unten, dazwischen die Routing-Engine.

## 1. Korrekte Schichten-Trennung

```mermaid
graph TB
    subgraph CLIENT["Client (pyads / AdsClient)"]
        APP["Application Code<br/>plc.read('MAIN.var')"]
        AMS_LIB["AMS Layer<br/>baut AMS-Frame mit<br/>Source-NetId, Target-NetId, Cmd"]
    end

    subgraph ROUTER["Router Service (TC1000-Equivalent)"]
        IPC_IN["Local IPC<br/>akzeptiert AMS-Frames<br/>(Win32 Window / TCP-Loopback)"]
        ROUTING["Routing-Decision<br/>Target-NetId -> StaticRoutes.xml"]
        TX_TCP["TCP Transport-Adapter<br/>wraps AMS in TCP-AMS-Frame"]
        TX_MQTT["MQTT Transport-Adapter<br/>wraps AMS in MQTT-Payload<br/>publish Topic AdsOverMqtt/target/ams"]
        TX_UDP["UDP Transport-Adapter"]
    end

    subgraph NET["Netzwerk"]
        TCP_NET["TCP / 48898"]
        BROKER["MQTT Broker"]
        UDP_NET["UDP"]
    end

    subgraph REMOTE["Remote (TwinCAT Runtime)"]
        REMOTE_RT["Remote Router<br/>unwraps Transport"]
        AMS_TARGET["AMS-Frame an lokalen<br/>AMS-Server-Port (851)"]
        TC_RT["TwinCAT Runtime<br/>processiert ADS-Command"]
    end

    APP --> AMS_LIB
    AMS_LIB -->|AMS-Frame| IPC_IN
    IPC_IN --> ROUTING
    ROUTING -->|"Type=TCP_IP"| TX_TCP
    ROUTING -->|"Type=MQTT"| TX_MQTT
    ROUTING -->|"Type=AdsUdp"| TX_UDP
    TX_TCP --> TCP_NET
    TX_MQTT --> BROKER
    TX_UDP --> UDP_NET
    TCP_NET --> REMOTE_RT
    BROKER --> REMOTE_RT
    UDP_NET --> REMOTE_RT
    REMOTE_RT --> AMS_TARGET
    AMS_TARGET --> TC_RT

    style ROUTING fill:#ff9999
    style TX_MQTT fill:#ffcc99
```

**Rot:** Routing-Decision liest Type-Feld aus Route-Eintrag und dispatcht.
**Orange:** MQTT-Transport-Adapter — das fehlende Stueck im NuGet-Stack.

## 2. Pyads-Schicht-Klaerung

```mermaid
graph LR
    subgraph LIN["Linux / WSL"]
        PYL["pyads<br/>(Python)"] -->|TCP-Loopback| ADSL["adslib.so<br/>(bundled)"]
        ADSL -->|TCP 48898| ROUTER1["Router Service"]
    end

    subgraph WTC["Windows mit TwinCAT"]
        PYW1["pyads<br/>(Python)"] -->|Win32 Window Msg| TCD1["TcAdsDll.dll"]
        TCD1 -->|IPC TcAmsWindow| TCSYS["TcSystemServiceUm.exe<br/>(TwinCAT Setup)"]
    end

    subgraph WONLY["Windows ohne TwinCAT"]
        PYW2["pyads<br/>(Python)"] -->|Win32 Window Msg| TCD2["TcAdsDll.dll"]
        TCD2 -. "findet kein" .-> X["TcAmsWindow"]
        X -.- LUECKE["LUECKE<br/>kein TC-Service"]
    end

    style WONLY fill:#ffcccc
    style LIN fill:#ccffcc
```

Linux/WSL pyads spricht bereits TCP-AMS — kompatibel mit unserem Router. Windows-pyads ohne TwinCAT geht prinzipiell nicht (TcAdsDll braucht Win32-Window).

## 3. Was Beckhoff im NuGet-Stack tatsaechlich liefert

| Layer | Komponente | Rolle |
|-------|-----------|-------|
| AMS-Library | `Beckhoff.TwinCAT.Ads` (AdsClient) | baut AMS-Frames |
| Local IPC In | `AmsTcpIpRouter` TCP-Loopback | empfaengt AMS-Frames lokal |
| Routing | `AmsTcpIpRouter` interne Route-Engine | **nur TCP-Lookup** |
| Transport TCP | im AmsTcpIpRouter integriert | OK |
| Transport MQTT | `Beckhoff.TwinCAT.Ads.AdsOverMqtt` Plugin | **nur als Server-Endpoint, nicht als Forward-Adapter** |
| Transport UDP | `Beckhoff.TwinCAT.Ads.AdsUdp` separate | nur Discovery, nicht Routing |

Beckhoff hat alle Bausteine — die **Glue** zwischen Routing-Engine und MQTT-Adapter fehlt. Im echten TwinCAT-System-Service ist sie integriert. Im Open-Source-NuGet nicht.

## 4. Bewiesene End-to-End-Faehigkeit (Durchstich)

```mermaid
sequenceDiagram
    participant TC as AdsTestClient .NET
    participant Plugin as AdsOverMqtt Plugin
    participant Broker as Mosquitto 1883
    participant PLC as TwinCAT Runtime

    TC->>Plugin: ReadDeviceInfo
    Plugin->>Broker: subscribe own NetId topic
    Plugin->>Broker: publish to PLC NetId topic
    Broker->>PLC: Deliver Request
    PLC->>PLC: Process
    PLC->>Broker: publish Response
    Broker->>Plugin: Deliver Response
    Plugin->>TC: DeviceInfo Plc30 App AdsState Run
```

Ergebnis: `AdsTestClient.exe` liest PLC-Daten via MQTT ohne TC-Install auf Host.
Code in `AdsTestClient/Program.cs`. Beweis dass MQTT-Payload-Wrapping + Topic-Schema funktioniert.

## 5. Custom-Bridge-Architektur (zu bauen)

```mermaid
graph TB
    subgraph WHOST["Windows Host"]
        PY["pyads<br/>via WSL TCP-Loopback"]

        subgraph BRIDGE["AdsRouter+ (Custom)"]
            TCPL["AmsTcpIpRouter<br/>TCP-Loopback :48898"]
            INT["Frame-Interceptor<br/>liest Target-NetId<br/>aus AMS-Header"]
            ROUTES["StaticRoutes.xml-Lookup"]
            TCPADAPT["TCP-Adapter<br/>(im AmsTcpIpRouter)"]
            MQTTADAPT["MQTT-Adapter<br/>nutzt AdsOverMqtt-<br/>Library-Calls"]
        end
    end

    subgraph PLCBOX["PLC"]
        BR["Mosquitto :1883"]
        RT["TwinCAT Runtime"]
    end

    subgraph OTHER["TCP-PLC"]
        OT["andere TwinCAT"]
    end

    PY -->|AMS-Frame TCP| TCPL
    TCPL --> INT
    INT --> ROUTES
    ROUTES -->|"Type TCP_IP"| TCPADAPT
    ROUTES -->|"Type MQTT"| MQTTADAPT
    MQTTADAPT <--> BR
    BR <--> RT
    TCPADAPT --> OT

    style INT fill:#ffe699
    style MQTTADAPT fill:#ffe699
```

**Gelb:** Code den wir schreiben.

## 6. MqttRouteHandler — Implementation-Skizze

Was wir aufbauen wuerden:

```csharp
// 1. Eigene Komponente die in den AmsTcpIpRouter Frame-Flow eingreift
public class MqttRouteHandler
{
    private readonly IMqttClient _mqttClient;
    private readonly RouteCollection _routes;
    private readonly Dictionary<AmsNetId, TaskCompletionSource<AmsFrame>> _pending;

    // Subscribe: AdsOverMqtt/<unsereNetId>/ams/#
    // Topics-Schema bereits in AdsOverMqtt-Plugin definiert
    public async Task StartAsync(...) { ... }

    // Wird aufgerufen wenn AmsTcpIpRouter ein Frame fuer eine MQTT-Route hat
    public async Task<AmsFrame?> ForwardAsync(AmsFrame request)
    {
        var topic = $"{_baseTopic}/{request.TargetNetId}/ams";
        var tcs = new TaskCompletionSource<AmsFrame>();
        _pending[request.InvokeId] = tcs;

        // AMS-Frame als MQTT-Payload publishen
        await _mqttClient.PublishAsync(new MqttApplicationMessage
        {
            Topic = topic,
            Payload = SerializeAmsFrame(request)
        });

        return await tcs.Task;
    }

    // MQTT-Subscriber-Handler: response zurueckliefern
    private void OnMqttMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var frame = ParseAmsFrame(e.ApplicationMessage.Payload);
        if (_pending.TryGetValue(frame.InvokeId, out var tcs))
            tcs.SetResult(frame);
    }
}

// 2. Hooks in AmsTcpIpRouter
//    Option A: Replace AmsTcpIpRouter komplett (eigener Frame-Listener auf 48898)
//    Option B: Subclass AmsTcpIpRouter und overriden des Routing-Pfads
//             (falls Beckhoff virtual-Methoden offen laesst, siehe Reflection)
//    Option C: Implement IRouteHandler-Interface falls existent
```

**Realistische Option:** Eigener TCP-Listener auf 48898 + AMS-Frame-Parser + per-Route-Type-Dispatcher. AdsOverMqtt-Plugin als Library nutzen, nicht als MEF-Plugin.

## 7. Status-Zusammenfassung

| Komponente | Status |
|------------|--------|
| AmsTcpIpRouter TCP-Listener | OK |
| AdsOverMqtt Plugin geladen | OK |
| MQTT-Broker-Anbindung | OK |
| Lokale Server-Endpoints (Port 1, 10000) MQTT-faehig | OK |
| **Forwarding fremder NetIds via MQTT** | **FEHLT — Custom-Code** |
| pyads -> Router -> MQTT -> PLC End-to-End | wartet auf MqttRouteHandler |
| .NET AdsClient -> MQTT -> PLC | OK Durchstich |

## 8. Naechste Schritte

1. AMS-Frame-Format dokumentieren (AMS-Header, AMS/TCP-Wrapper, MQTT-Payload-Schema)
2. TCP-Frame-Receiver schreiben (Python-Client connectet, Frame-Demarshal)
3. Routing-Engine schreiben (RouteCollection lookup, Type-Dispatch)
4. MQTT-Adapter schreiben (Publish AdsOverMqtt/target/ams, Subscribe self/ams/#)
5. TCP-Adapter (kann via AdsClient delegated werden)
6. Roundtrip-Test: pyads-Read-Request -> Response zurueck
