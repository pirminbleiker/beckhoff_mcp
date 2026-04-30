using System.Xml.Linq;

namespace BeckhoffMcp.AdsBridge.Bridge;

public enum TransportType
{
    TcpIp,
    Mqtt,
}

public sealed record RouteEntry(string Name, AmsNetId NetId, string Address, int Port, TransportType Type);

public sealed record MqttBrokerConfig(string Address, int Port, string Topic, bool Unidirectional);

public sealed class RouteTable
{
    public AmsNetId LocalNetId { get; private set; }
    public string LocalName { get; private set; } = "AdsBridge";
    public IReadOnlyList<RouteEntry> Routes { get; private set; } = Array.Empty<RouteEntry>();
    public MqttBrokerConfig? Mqtt { get; private set; }

    public static RouteTable LoadFromXml(string path)
    {
        var doc = XDocument.Load(path);
        var t = new RouteTable();

        var local = doc.Root?.Element("Local");
        if (local != null)
        {
            t.LocalName = local.Element("Name")?.Value ?? t.LocalName;
            var netId = local.Element("NetId")?.Value;
            if (!string.IsNullOrEmpty(netId)) t.LocalNetId = AmsNetId.Parse(netId);
        }

        var remote = doc.Root?.Element("RemoteConnections");
        if (remote != null)
        {
            var routes = new List<RouteEntry>();
            foreach (var r in remote.Elements("Route"))
            {
                var name = r.Element("Name")?.Value ?? "";
                var addr = r.Element("Address")?.Value ?? "";
                var nidStr = r.Element("NetId")?.Value ?? "";
                var typeStr = r.Element("Type")?.Value ?? "TCP_IP";
                var portStr = r.Element("Port")?.Value;
                var port = int.TryParse(portStr, out var p) ? p : 48898;
                var type = typeStr.Equals("MQTT", StringComparison.OrdinalIgnoreCase) ? TransportType.Mqtt : TransportType.TcpIp;
                if (!string.IsNullOrEmpty(nidStr))
                {
                    routes.Add(new RouteEntry(name, AmsNetId.Parse(nidStr), addr, port, type));
                }
            }
            t.Routes = routes;

            var mqtt = remote.Element("Mqtt");
            if (mqtt != null)
            {
                var addr = mqtt.Element("Address");
                var address = addr?.Value ?? "";
                var port = int.TryParse(addr?.Attribute("Port")?.Value, out var pp) ? pp : 1883;
                var topic = mqtt.Element("Topic")?.Value ?? "AdsOverMqtt";
                var uniStr = mqtt.Attribute("Unidirectional")?.Value ?? "false";
                var uni = uniStr.Equals("true", StringComparison.OrdinalIgnoreCase);
                t.Mqtt = new MqttBrokerConfig(address, port, topic, uni);
            }
        }

        return t;
    }

    public RouteEntry? Resolve(AmsNetId target)
    {
        foreach (var r in Routes) if (r.NetId.Equals(target)) return r;
        return null;
    }
}
