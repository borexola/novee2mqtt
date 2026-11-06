using Novee2Mqtt.Ble;
using Novee2Mqtt.Core;
using Novee2Mqtt.Lan;

namespace Novee2Mqtt.Tests;

public class BleTransportTests
{
    private static byte Xor(ReadOnlySpan<byte> frame)
    {
        byte checksum = 0;
        foreach (var b in frame[..19])
        {
            checksum ^= b;
        }
        return checksum;
    }

    private static readonly BleLightProfile Bulb = BleLightProfile.Resolve("H6008");
    private static readonly BleLightProfile Backlight = BleLightProfile.Resolve("H66A1");
    private static readonly BleLightProfile Legacy = BleLightProfile.Resolve("H6127");

    private static byte[] Hex(string text) => Convert.FromHexString(text);

    // These frames are the Govee app's own, from HCI captures published in
    // egold555/Govee-Reverse-Engineering and Beshelmek/govee_ble_lights#34.
    [Fact]
    public void BulbFramesMatchTheAppByteForByte()
    {
        Assert.Equal(Hex("3301010000000000000000000000000000000033"), LightFrames.Power(true));
        Assert.Equal(Hex("3301000000000000000000000000000000000032"), LightFrames.Power(false));
        Assert.Equal(Hex("3304640000000000000000000000000000000053"), LightFrames.Brightness(Bulb, 100));
        Assert.Equal(Hex("33050dff000000000000000000000000000000c4"), LightFrames.ColorRgb(Bulb, new DeviceColor(255, 0, 0)));
        Assert.Equal(Hex("33050dffae540a8cffae540000000000000000bd"), LightFrames.ColorTemperature(Bulb, 2700));
        Assert.Equal(Hex("33050dfff9fb1964fff9fb000000000000000046"), LightFrames.ColorTemperature(Bulb, 6500));
    }

    [Fact]
    public void BrightnessScalesPerProfile()
    {
        Assert.Equal(50, LightFrames.Brightness(Bulb, 50)[2]);
        Assert.Equal(100, LightFrames.Brightness(Bulb, 250)[2]);
        Assert.Equal(1, LightFrames.Brightness(Bulb, 0)[2]);

        Assert.Equal(20, LightFrames.Brightness(Legacy, 1)[2]);
        Assert.Equal(254, LightFrames.Brightness(Legacy, 100)[2]);
    }

    [Fact]
    public void BacklightUsesItsFourteenSegmentMask()
    {
        var rgb = LightFrames.ColorRgb(Backlight, new DeviceColor(0x10, 0x20, 0x30));
        Assert.Equal(Hex("3305150110203000000000" + "00ff3f"), rgb[..14]);
        Assert.Equal(PacketManager.Checksum(rgb.AsSpan(0, 19)), rgb[19]);

        var white = LightFrames.ColorTemperature(Backlight, 4000);
        Assert.Equal(Hex("33051501ffffff0fa0ffd5a1ff3f"), white[..14]);
    }

    [Fact]
    public void LegacyWhiteSetsTheFlagAndTheTint()
    {
        Assert.Equal(Hex("330502ffffff01ffae54"), LightFrames.ColorTemperature(Legacy, 2700)[..10]);
    }

    [Theory]
    [InlineData(1500, 2700)]
    [InlineData(7500, 6500)]
    [InlineData(4049, 4000)]
    [InlineData(4050, 4100)]
    public void KelvinIsClampedThenRoundedToTheAppsGrid(int requested, int sent)
    {
        Assert.Equal(sent, LightFrames.NormalizeKelvin(requested, Bulb));
    }

    [Fact]
    public void TintsComeFromTheAppsTable()
    {
        Assert.Equal(new DeviceColor(0xff, 0x33, 0x00), LightFrames.KelvinTint(1000));
        Assert.Equal(new DeviceColor(0xff, 0xf9, 0xfb), LightFrames.KelvinTint(6500));
        Assert.Equal(new DeviceColor(0xcf, 0xda, 0xff), LightFrames.KelvinTint(10000));
        Assert.Equal(DeviceColor.Black, LightFrames.KelvinTint(4050));
    }

    [Fact]
    public void QueriesMatchTheApp()
    {
        Assert.Equal(Hex("aa010000000000000000000000000000000000ab"), LightFrames.KeepAlive());
        Assert.Equal([0xaa, 0x04], LightFrames.QueryBrightness()[..2]);
        Assert.Equal([0xaa, 0x05, 0x01], LightFrames.QueryColor()[..3]);
    }

    [Fact]
    public void RepliesUpdateOnlyTheFieldTheyCarry()
    {
        var status = new LanDeviceStatus(false, 10, new DeviceColor(1, 2, 3), 0);

        status = LightFrames.ApplyReply(Bulb, status, PacketManager.Frame([0xaa, 0x01, 0x01]))!;
        Assert.True(status.On);
        Assert.Equal(10, status.Brightness);
        Assert.Equal(new DeviceColor(1, 2, 3), status.Color);

        status = LightFrames.ApplyReply(Bulb, status, PacketManager.Frame([0xaa, 0x04, 75]))!;
        Assert.Equal(75, status.Brightness);

        status = LightFrames.ApplyReply(Bulb, status, PacketManager.Frame([0xaa, 0x05, 0x0d, 0x10, 0x20, 0x30, 0, 0, 0, 0, 0]))!;
        Assert.Equal(new DeviceColor(0x10, 0x20, 0x30), status.Color);
        Assert.Equal(0, status.ColorTemperatureKelvin);

        status = LightFrames.ApplyReply(Bulb, status, PacketManager.Frame([0xaa, 0x05, 0x0d, 0xff, 0xd5, 0xa1, 0x0f, 0xa0, 0xff, 0xd5, 0xa1]))!;
        Assert.Equal(4000, status.ColorTemperatureKelvin);

        Assert.Null(LightFrames.ApplyReply(Bulb, status, PacketManager.Frame([0x33, 0x01, 0x01])));
        Assert.Null(LightFrames.ApplyReply(Bulb, status, PacketManager.Frame([0xaa, 0x11, 0x01])));
    }

    [Fact]
    public void NegativeKelvinShowsTheTint()
    {
        var status = LightFrames.ApplyReply(Bulb, LanDeviceStatus.Empty,
            PacketManager.Frame([0xaa, 0x05, 0x0d, 1, 2, 3, 0xff, 0xff, 7, 8, 9]))!;

        Assert.Equal(new DeviceColor(7, 8, 9), status.Color);
    }

    [Fact]
    public void BacklightModeReportLeavesTheColourAlone()
    {
        var red = new DeviceColor(255, 0, 0);
        var status = new LanDeviceStatus(true, 50, red, 0);

        // aa 05 15: the gradual flag and a Kelvin, with no colour in it.
        Assert.Equal(red, LightFrames.ApplyReply(Backlight, status, PacketManager.Frame([0xaa, 0x05, 0x15, 0x01, 0, 0]))!.Color);

        var white = LightFrames.ApplyReply(Backlight, status, PacketManager.Frame([0xaa, 0x05, 0x15, 0x01, 0x0f, 0xa0]))!;
        Assert.Equal(4000, white.ColorTemperatureKelvin);
    }

    [Fact]
    public void SceneModeReportChangesNothingButIsStillAnAnswer()
    {
        var status = new LanDeviceStatus(true, 50, new DeviceColor(255, 0, 0), 0);

        Assert.Equal(status, LightFrames.ApplyReply(Bulb, status, PacketManager.Frame([0xaa, 0x05, 0x04, 0x11])));
    }

    [Fact]
    public void LegacyBrightnessReplyIsScaledBack()
    {
        Assert.Equal(100, LightFrames.ApplyReply(Legacy, LanDeviceStatus.Empty, PacketManager.Frame([0xaa, 0x04, 0xfe]))!.Brightness);
        Assert.Equal(1, LightFrames.ApplyReply(Legacy, LanDeviceStatus.Empty, PacketManager.Frame([0xaa, 0x04, 0x14]))!.Brightness);
    }

    // Computed independently, with Python's cryptography package and a separate RC4.
    [Fact]
    public void CipherMatchesAnIndependentImplementation()
    {
        var frame = LightFrames.Power(true);

        Assert.Equal(Hex("7e23fe355df5017a39440e3574a16ab868ee406c"), new GoveeCipher(GoveeCipher.HandshakeKey).Encrypt(frame));

        var session = new GoveeCipher(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        Assert.Equal(Hex("71a5d00cbe4136cf6d8a60dec77d07d8e99c40ca"), session.Encrypt(frame));
        Assert.Equal(frame, session.Decrypt(Hex("71a5d00cbe4136cf6d8a60dec77d07d8e99c40ca")));
    }

    [Fact]
    public void HandshakeRepliesAreRecognised()
    {
        var reply = Hex("4d59159c56527d09941c0344c948c99166e140b9");

        Assert.Equal(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(), GoveeCipher.ParseSessionKey(reply));
        Assert.False(GoveeCipher.IsConfirmReply(reply));
        Assert.Null(GoveeCipher.ParseSessionKey(LightFrames.Power(true)));

        var psk = new GoveeCipher(GoveeCipher.HandshakeKey);
        var request = psk.Decrypt(GoveeCipher.BuildHandshakeFrame(GoveeCipher.RequestSessionKey));
        Assert.Equal([0xe7, 0x01], request[..2]);
        Assert.Equal(PacketManager.Checksum(request.AsSpan(0, 19)), request[19]);
    }

    private static KeyValuePair<ushort, byte[]> Advert(ushort companyId, params byte[] value) => new(companyId, value);

    [Fact]
    public void EncryptFlagComesFromTheManufacturerData()
    {
        Assert.False(BleNaming.EncryptFlag([Advert(0x8801, 0xec, 0x00, 0x01, 0x01, 0x00)]));
        Assert.True(BleNaming.EncryptFlag([Advert(0x8841, 0xec, 0x00, 0x01, 0x01, 0x00)]));
        Assert.Null(BleNaming.EncryptFlag([Advert(0x4c00, 0x02, 0x15, 0x00, 0x00)]));
        Assert.Null(BleNaming.EncryptFlag([Advert(0x8801, 0xec)]));
        Assert.Null(BleNaming.EncryptFlag([]));
    }

    [Fact]
    public void EntriesThatDisagreeGiveNoFlag()
    {
        Assert.Null(BleNaming.EncryptFlag([
            Advert(0x8801, 0xec, 0x00, 0x01, 0x01),
            Advert(0x8841, 0xec, 0x00, 0x01, 0x01),
        ]));
    }

    [Fact]
    public void TheDeviceIdCarriesTheWifiMacOneBelowTheBluetoothAddress()
    {
        Assert.Equal(
            ["5C:E7:53:BC:97:9D", "5C:E7:53:BC:97:9C"],
            BleNaming.AddressCandidates("2C:67:5C:E7:53:BC:97:9C"));

        Assert.Equal("C0:17:55:BF:B3:00", BleNaming.AddressCandidates("0E:81:C0:17:55:BF:B2:FF").First());
        Assert.Empty(BleNaming.AddressCandidates("A4:C1:38:11:22:33"));
        Assert.Empty(BleNaming.AddressCandidates("not-an-id"));
        Assert.Null(BleNaming.MacFromDeviceId("not-an-id"));
    }

    [Theory]
    [InlineData("ihoment_H6008_979C", "H6008")]
    [InlineData("GBK_H66A1_B2A4", "H66A1")]
    [InlineData("Govee_H6102_1234", "H6102")]
    [InlineData("GVH5075_1234", "H5075")]
    [InlineData("Living room speaker", null)]
    [InlineData("H6008", null)]
    [InlineData(null, null)]
    public void SkuIsReadFromTheAdvertisedName(string? name, string? expected)
    {
        Assert.Equal(expected, BleNaming.SkuFromName(name));
    }

    [Theory]
    [InlineData("5c:e7:53:bc:97:9c", "5C:E7:53:BC:97:9C")]
    [InlineData("5C-E7-53-BC-97-9C", "5C:E7:53:BC:97:9C")]
    [InlineData("5CE753BC979C", "5C:E7:53:BC:97:9C")]
    [InlineData("5C:E7:53:BC:97", null)]
    [InlineData("5C:E7:53:BC:97:9G", null)]
    public void AddressesAreNormalised(string input, string? expected)
    {
        Assert.Equal(expected, BleNaming.NormalizeAddress(input));
    }

    [Fact]
    public void DevicePathsRoundTrip()
    {
        var path = BleNaming.DevicePath("/org/bluez/hci0", "5C:E7:53:BC:97:9C");

        Assert.Equal("/org/bluez/hci0/dev_5C_E7_53_BC_97_9C", path);
        Assert.Equal("5C:E7:53:BC:97:9C", BleNaming.AddressFromPath(path));
        Assert.Null(BleNaming.AddressFromPath(path + "/service0010/char0011"));
        Assert.Null(BleNaming.AddressFromPath("/org/bluez/hci0"));
    }

    [Theory]
    [InlineData("H6008", BleColorMode.ManualWhite, true, 16)]
    [InlineData("H6006", BleColorMode.ManualWhite, true, 16)]
    [InlineData("H66A1", BleColorMode.Segments, true, 14)]
    [InlineData("H6127", BleColorMode.Manual, false, 16)]
    [InlineData("H6159", BleColorMode.Manual, false, 16)]
    [InlineData("H619A", BleColorMode.Segments, true, 16)]
    [InlineData("H7050", BleColorMode.Segments, true, 16)]
    public void ProfilesComeFromTheTables(string sku, BleColorMode mode, bool percent, int segments)
    {
        var profile = BleLightProfile.Resolve(sku);

        Assert.Equal(mode, profile.ColorMode);
        Assert.Equal(percent, profile.BrightnessAsPercent);
        Assert.Equal(segments, profile.Segments);
    }

    [Fact]
    public void ProfileOverridesParse()
    {
        var overrides = BleLightProfile.ParseOverrides("H66A1=0d, H1234=02/byte,H9999=0x15/percent");

        Assert.Equal(BleColorMode.ManualWhite, overrides["H66A1"].ColorMode);
        Assert.Equal(14, overrides["H66A1"].Segments);
        Assert.Equal(BleColorMode.Manual, overrides["H1234"].ColorMode);
        Assert.False(overrides["H1234"].BrightnessAsPercent);
        Assert.Equal(BleColorMode.Segments, overrides["H9999"].ColorMode);

        Assert.Throws<GoveeException>(() => BleLightProfile.ParseOverrides("H66A1"));
        Assert.Throws<GoveeException>(() => BleLightProfile.ParseOverrides("H66A1=99"));
        Assert.Throws<GoveeException>(() => BleLightProfile.ParseOverrides("H66A1=0d/sometimes"));
    }

    [Fact]
    public void AddressOverridesParse()
    {
        var addresses = BleOptions.ParseAddresses("2C:67:5C:E7:53:BC:97:9C=aa-bb-cc-dd-ee-ff, X=112233445566");

        Assert.Equal("AA:BB:CC:DD:EE:FF", addresses["2C:67:5C:E7:53:BC:97:9C"]);
        Assert.Equal("11:22:33:44:55:66", addresses["X"]);

        Assert.Throws<GoveeException>(() => BleOptions.ParseAddresses("2C:67=nope"));
        Assert.Throws<GoveeException>(() => BleOptions.ParseAddresses("justanaddress"));
    }
}
