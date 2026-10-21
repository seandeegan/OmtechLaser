using System.Text;
using RJCP.IO.Ports;

namespace LaserPilot.Core.Comm;

/// <summary>
/// The byte pipe <see cref="GrblController"/> talks through. The real
/// implementation wraps a serial port; tests substitute a simulated GRBL
/// controller so the streaming protocol can be verified without hardware.
/// </summary>
public interface ISerialTransport : IDisposable
{
    string PortName { get; }
    bool IsOpen { get; }
    bool DtrEnable { get; set; }
    bool RtsEnable { get; set; }
    void Open();
    void Close();
    void DiscardInBuffer();
    void DiscardOutBuffer();

    /// <summary>Blocks for up to the read timeout. Returns 0 when nothing arrived
    /// (timeout) — and also, immediately, when the device has gone away.</summary>
    int Read(byte[] buffer, int offset, int count);

    void Write(byte[] buffer, int offset, int count);
    void Flush();
}

/// <summary>Serial-port transport (RJCP SerialPortStream).</summary>
internal sealed class SerialPortTransport : ISerialTransport
{
    private readonly SerialPortStream _port;

    public SerialPortTransport(string portName, int baud)
    {
        // LaserGRBL leaves these control lines off for normal GRBL connections.
        // Some USB controllers use DTR/RTS as reset or enable pins and can be
        // held in reset when they are asserted continuously.
        _port = new SerialPortStream(portName, baud, 8, Parity.None, StopBits.One)
        {
            NewLine = "\n",
            ReadTimeout = 500,
            WriteTimeout = 2000,
            Handshake = Handshake.None,
            DtrEnable = false,
            RtsEnable = false,
            Encoding = Encoding.ASCII,
        };
    }

    public string PortName => _port.PortName;
    public bool IsOpen => _port.IsOpen;
    public bool DtrEnable { get => _port.DtrEnable; set => _port.DtrEnable = value; }
    public bool RtsEnable { get => _port.RtsEnable; set => _port.RtsEnable = value; }
    public void Open() => _port.Open();
    public void Close() => _port.Close();
    public void DiscardInBuffer() => _port.DiscardInBuffer();
    public void DiscardOutBuffer() => _port.DiscardOutBuffer();
    public int Read(byte[] buffer, int offset, int count) => _port.Read(buffer, offset, count);
    public void Write(byte[] buffer, int offset, int count) => _port.Write(buffer, offset, count);
    public void Flush() => _port.Flush();
    public void Dispose() => _port.Dispose();
}
