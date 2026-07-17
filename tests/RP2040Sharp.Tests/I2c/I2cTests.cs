using RP2040.Peripherals.I2c;

namespace RP2040.Peripherals.Tests.I2c;

/// <summary>
/// Tests for the DW_apb_i2c peripheral, focusing on the slave-mode simulation API
/// used by external circuit simulators.
/// </summary>
public abstract class I2cTests
{
    private const uint IC_SAR           = 0x008;
    private const uint IC_DATA_CMD      = 0x010;
    private const uint IC_RAW_INTR_STAT = 0x034;
    private const uint IC_CLR_TX_ABRT   = 0x054;
    private const uint IC_CLR_RD_REQ    = 0x050;
    private const uint IC_CLR_STOP_DET  = 0x060;
    private const uint IC_ENABLE        = 0x06C;
    private const uint IC_TX_ABRT_SOURCE = 0x080;

    // IC_RAW_INTR_STAT bits
    private const uint RX_FULL  = 1u << 2;
    private const uint TX_EMPTY = 1u << 4;
    private const uint RD_REQ   = 1u << 5;
    private const uint TX_ABRT  = 1u << 6;
    private const uint STOP_DET = 1u << 9;

    private static I2cPeripheral Enabled(byte slaveAddr = 0x55)
    {
        var i2c = new I2cPeripheral();
        i2c.WriteWord(IC_SAR, slaveAddr);
        i2c.WriteWord(IC_ENABLE, 1);
        return i2c;
    }

    public class SlaveAddress
    {
        [Fact]
        public void Writing_IC_SAR_updates_SlaveAddress_and_masks_to_7_bits()
        {
            var i2c = new I2cPeripheral();
            i2c.WriteWord(IC_SAR, 0x1A5);   // 0x25 in low 7 bits
            i2c.SlaveAddress.Should().Be(0x25);
        }

        [Fact]
        public void Writing_IC_SAR_raises_SlaveAddressChanged()
        {
            var i2c = new I2cPeripheral();
            byte? notified = null;
            i2c.SlaveAddressChanged += a => notified = a;

            i2c.WriteWord(IC_SAR, 0x42);

            notified.Should().Be(0x42);
        }
    }

    public class SlaveReceive
    {
        [Fact]
        public void Matching_write_address_is_acknowledged()
        {
            var i2c = Enabled(0x55);
            i2c.SimulateIncomingAddress(0x55, isWrite: true).Should().BeTrue();
        }

        [Fact]
        public void Non_matching_address_is_not_acknowledged()
        {
            var i2c = Enabled(0x55);
            i2c.SimulateIncomingAddress(0x10, isWrite: true).Should().BeFalse();
        }

        [Fact]
        public void Incoming_data_raises_RX_FULL_and_lands_in_FIFO()
        {
            var i2c = Enabled(0x55);
            i2c.SimulateIncomingAddress(0x55, isWrite: true);

            i2c.SimulateIncomingData(0xAB);

            (i2c.ReadWord(IC_RAW_INTR_STAT) & RX_FULL).Should().Be(RX_FULL);
            i2c.ReadWord(IC_DATA_CMD).Should().Be(0xABu, "firmware reads the received byte from IC_DATA_CMD");
        }

        [Fact]
        public void Stop_raises_STOP_DET()
        {
            var i2c = Enabled(0x55);
            i2c.SimulateIncomingAddress(0x55, isWrite: true);
            i2c.SimulateIncomingData(0x01);

            i2c.SimulateStop();

            (i2c.ReadWord(IC_RAW_INTR_STAT) & STOP_DET).Should().Be(STOP_DET);
        }
    }

    public class SlaveTransmit
    {
        [Fact]
        public void Read_address_raises_RD_REQ()
        {
            var i2c = Enabled(0x55);

            i2c.SimulateIncomingAddress(0x55, isWrite: false).Should().BeTrue();

            (i2c.ReadWord(IC_RAW_INTR_STAT) & RD_REQ).Should().Be(RD_REQ);
        }

        [Fact]
        public void Firmware_response_is_captured_and_sets_TX_EMPTY()
        {
            var i2c = Enabled(0x55);
            i2c.SimulateIncomingAddress(0x55, isWrite: false);

            i2c.WriteWord(IC_DATA_CMD, 0x7E);   // firmware responds with one byte

            (i2c.ReadWord(IC_RAW_INTR_STAT) & TX_EMPTY).Should().Be(TX_EMPTY);
            i2c.HasSlaveTransmitByte.Should().BeTrue();
            i2c.ReadSlaveTransmitByte().Should().Be(0x7E);
            i2c.HasSlaveTransmitByte.Should().BeFalse();
        }

        [Fact]
        public void Multiple_bytes_are_queued_in_order_until_stop()
        {
            var i2c = Enabled(0x55);
            i2c.SimulateIncomingAddress(0x55, isWrite: false);

            // Master clocks out three bytes before issuing STOP.
            i2c.WriteWord(IC_DATA_CMD, 0x11);
            i2c.WriteWord(IC_DATA_CMD, 0x22);
            i2c.WriteWord(IC_DATA_CMD, 0x33);

            i2c.ReadSlaveTransmitByte().Should().Be(0x11);
            i2c.ReadSlaveTransmitByte().Should().Be(0x22);
            i2c.ReadSlaveTransmitByte().Should().Be(0x33);
        }

        [Fact]
        public void Stop_ends_slave_transmit_so_later_writes_are_master_writes()
        {
            var i2c = Enabled(0x55);
            i2c.SimulateIncomingAddress(0x55, isWrite: false);
            i2c.WriteWord(IC_DATA_CMD, 0x11);
            i2c.SimulateStop();
            i2c.ReadSlaveTransmitByte();   // drain the queued byte

            byte? written = null;
            i2c.OnWrite = (_, data) => written = data;

            // After STOP the slave-transmit phase is over: this is a master write.
            i2c.WriteWord(IC_DATA_CMD, 0x99);

            written.Should().Be(0x99);
            i2c.HasSlaveTransmitByte.Should().BeFalse();
        }
    }

    public class MasterNack
    {
        [Fact]
        public void SignalAddressNack_raises_TX_ABRT()
        {
            var i2c = Enabled();
            i2c.SignalAddressNack();
            (i2c.ReadWord(IC_RAW_INTR_STAT) & TX_ABRT).Should().NotBe(0);
        }

        [Fact]
        public void SignalAddressNack_sets_ABRT_7B_ADDR_NOACK_source()
        {
            var i2c = Enabled();
            i2c.SignalAddressNack();
            (i2c.ReadWord(IC_TX_ABRT_SOURCE) & 0x1u).Should().NotBe(0);
        }

        [Fact]
        public void Reading_IC_CLR_TX_ABRT_clears_interrupt_and_source()
        {
            var i2c = Enabled();
            i2c.SignalAddressNack();

            i2c.ReadWord(IC_CLR_TX_ABRT);   // clear-on-read

            (i2c.ReadWord(IC_RAW_INTR_STAT) & TX_ABRT).Should().Be(0);
            i2c.ReadWord(IC_TX_ABRT_SOURCE).Should().Be(0);
        }
    }
}
