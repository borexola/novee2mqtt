using Tmds.DBus.Protocol;

namespace Novee2Mqtt.Ble;

/// <summary>
/// The handful of BlueZ D-Bus calls the Bluetooth transport needs, written
/// directly against the protocol layer. Object paths are plain strings
/// throughout; the shapes come from the BlueZ D-Bus API documentation.
/// </summary>
internal sealed class BlueZBus : IDisposable
{
    public const string Service = "org.bluez";
    public const string AdapterInterface = "org.bluez.Adapter1";
    public const string DeviceInterface = "org.bluez.Device1";
    public const string ServiceInterface = "org.bluez.GattService1";
    public const string CharacteristicInterface = "org.bluez.GattCharacteristic1";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";

    /// <summary>Object path → interface name → property name → value.</summary>
    public sealed class ManagedObjects : Dictionary<string, Dictionary<string, Dictionary<string, VariantValue>>>
    {
        public ManagedObjects() : base(StringComparer.Ordinal) { }
    }

    private readonly DBusConnection _connection;

    private BlueZBus(DBusConnection connection) => _connection = connection;

    public static async Task<BlueZBus> ConnectAsync(CancellationToken cancellationToken)
    {
        var connection = new DBusConnection(SystemBusAddress());
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return new BlueZBus(connection);
    }

    /// <summary>
    /// The library's default goes through /var/run, which minimal images may
    /// not have, while the documented mount is /run/dbus.
    /// </summary>
    private static string SystemBusAddress()
    {
        const string socket = "/run/dbus/system_bus_socket";
        if (Environment.GetEnvironmentVariable("DBUS_SYSTEM_BUS_ADDRESS") is { Length: > 0 } configured)
        {
            return configured;
        }
        return File.Exists(socket) ? $"unix:path={socket}" : DBusAddress.System ?? $"unix:path={socket}";
    }

    public void Dispose() => _connection.Dispose();

    // ------------------------------------------------------------- method calls

    public Task<ManagedObjects> GetManagedObjectsAsync(CancellationToken cancellationToken)
        => Call(Build("/", ObjectManagerInterface, "GetManagedObjects", null, (ref MessageWriter _) => { }), ReadManagedObjects, cancellationToken);

    public Task CallAsync(string path, string iface, string member, CancellationToken cancellationToken, TimeSpan? timeout = null)
        => Call(Build(path, iface, member, null, (ref MessageWriter _) => { }), cancellationToken, timeout);

    public Task<Dictionary<string, VariantValue>> GetAllAsync(string path, string iface, CancellationToken cancellationToken)
        => Call(Build(path, PropertiesInterface, "GetAll", "s", (ref MessageWriter w) => w.WriteString(iface)),
            (Message m, object? _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue(), cancellationToken);

    public Task SetAsync(string path, string iface, string property, bool value, CancellationToken cancellationToken)
        => Call(Build(path, PropertiesInterface, "Set", "ssv", (ref MessageWriter w) =>
        {
            w.WriteString(iface);
            w.WriteString(property);
            w.WriteVariantBool(value);
        }), cancellationToken);

    /// <summary>Restricts discovery to LE devices; BlueZ otherwise also scans BR/EDR, which is slow.</summary>
    public Task SetDiscoveryFilterAsync(string adapterPath, CancellationToken cancellationToken)
        => Call(Build(adapterPath, AdapterInterface, "SetDiscoveryFilter", "a{sv}", (ref MessageWriter w) =>
        {
            var dict = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("Transport");
            w.WriteVariantString("le");
            w.WriteDictionaryEnd(dict);
        }), cancellationToken);

    /// <summary>
    /// Writes to a characteristic. <paramref name="writeType"/> is BlueZ's
    /// "type" option: <c>command</c> for write-without-response, or null to
    /// let BlueZ choose.
    /// </summary>
    public Task WriteValueAsync(string characteristicPath, byte[] value, string? writeType, CancellationToken cancellationToken)
        => Call(Build(characteristicPath, CharacteristicInterface, "WriteValue", "aya{sv}", (ref MessageWriter w) =>
        {
            w.WriteArray(value);
            var dict = w.WriteDictionaryStart();
            if (writeType is not null)
            {
                w.WriteDictionaryEntryStart();
                w.WriteString("type");
                w.WriteVariantString(writeType);
            }
            w.WriteDictionaryEnd(dict);
        }), cancellationToken, TimeSpan.FromSeconds(10));

    public Task<byte[]> ReadValueAsync(string characteristicPath, CancellationToken cancellationToken)
        => Call(Build(characteristicPath, CharacteristicInterface, "ReadValue", "a{sv}", (ref MessageWriter w) =>
        {
            var dict = w.WriteDictionaryStart();
            w.WriteDictionaryEnd(dict);
        }), (Message m, object? _) => m.GetBodyReader().ReadArrayOfByte(), cancellationToken);

    // ------------------------------------------------------------------ signals

    /// <summary>Fires when BlueZ adds an object: a newly seen device, or a resolved GATT characteristic.</summary>
    public ValueTask<IDisposable> WatchInterfacesAddedAsync(Action<string, Dictionary<string, Dictionary<string, VariantValue>>> handler)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Service,
            Interface = ObjectManagerInterface,
            Member = "InterfacesAdded",
        };

        return _connection.AddMatchAsync(rule, ReadInterfacesAdded, Handler<(string Path, Dictionary<string, Dictionary<string, VariantValue>> Interfaces)>(
            v => handler(v.Path, v.Interfaces)), false, ObserverFlags.None, null);
    }

    public ValueTask<IDisposable> WatchInterfacesRemovedAsync(Action<string, string[]> handler)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Service,
            Interface = ObjectManagerInterface,
            Member = "InterfacesRemoved",
        };

        return _connection.AddMatchAsync(rule, (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            return (Path: reader.ReadObjectPathAsString(), Interfaces: reader.ReadArrayOfString());
        }, Handler<(string Path, string[] Interfaces)>(v => handler(v.Path, v.Interfaces)), false, ObserverFlags.None, null);
    }

    /// <summary>
    /// Fires on property changes of <paramref name="iface"/> on any object below
    /// <paramref name="pathNamespace"/>: device RSSI and connection state under
    /// an adapter, characteristic values under a device.
    /// </summary>
    public ValueTask<IDisposable> WatchPropertiesChangedAsync(
        string pathNamespace,
        string iface,
        Action<string, Dictionary<string, VariantValue>> handler)
    {
        var rule = new MatchRule
        {
            Type = MessageType.Signal,
            Sender = Service,
            Interface = PropertiesInterface,
            Member = "PropertiesChanged",
            PathNamespace = pathNamespace,
            Arg0 = iface,
        };

        return _connection.AddMatchAsync(rule, (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadString(); // the interface, already matched by Arg0
            return (Path: m.PathAsString ?? "", Changed: reader.ReadDictionaryOfStringToVariantValue());
        }, Handler<(string Path, Dictionary<string, VariantValue> Changed)>(v => handler(v.Path, v.Changed)), false, ObserverFlags.None, null);
    }

    private static Action<Notification<T>> Handler<T>(Action<T> handler)
        => notification =>
        {
            if (notification.HasValue)
            {
                handler(notification.Value);
            }
        };

    // ------------------------------------------------------------------ helpers

    // The writer is a struct, so the body must take it by reference for its
    // writes to land in the buffer.
    private delegate void WriterAction(ref MessageWriter writer);

    private MessageBuffer Build(string path, string iface, string member, string? signature, WriterAction body)
    {
        var writer = _connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, path, iface, member, signature, MessageFlags.None);
            body(ref writer);
            return writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private Task Call(MessageBuffer message, CancellationToken cancellationToken, TimeSpan? timeout = null)
        => _connection.CallMethodAsync(message).WaitAsync(timeout ?? TimeSpan.FromSeconds(30), cancellationToken);

    private Task<T> Call<T>(MessageBuffer message, MessageValueReader<T> reader, CancellationToken cancellationToken)
        => _connection.CallMethodAsync(message, reader, null).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);

    private static ManagedObjects ReadManagedObjects(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        var result = new ManagedObjects();
        var outer = reader.ReadDictionaryStart();
        while (reader.HasNext(outer))
        {
            var path = reader.ReadObjectPathAsString();
            result[path] = ReadInterfaces(ref reader);
        }
        return result;
    }

    private static (string Path, Dictionary<string, Dictionary<string, VariantValue>> Interfaces) ReadInterfacesAdded(Message message, object? _)
    {
        var reader = message.GetBodyReader();
        var path = reader.ReadObjectPathAsString();
        return (path, ReadInterfaces(ref reader));
    }

    private static Dictionary<string, Dictionary<string, VariantValue>> ReadInterfaces(ref Reader reader)
    {
        var interfaces = new Dictionary<string, Dictionary<string, VariantValue>>(StringComparer.Ordinal);
        var inner = reader.ReadDictionaryStart();
        while (reader.HasNext(inner))
        {
            var iface = reader.ReadString();
            interfaces[iface] = reader.ReadDictionaryOfStringToVariantValue();
        }
        return interfaces;
    }

    /// <summary>True for the BlueZ errors that mean "already in that state".</summary>
    public static bool IsBenign(DBusErrorReplyException ex)
        => ex.ErrorName is "org.bluez.Error.InProgress" or "org.bluez.Error.AlreadyConnected"
           || (ex.ErrorName == "org.bluez.Error.Failed" && ex.ErrorMessage.Contains("No discovery started", StringComparison.OrdinalIgnoreCase));
}
