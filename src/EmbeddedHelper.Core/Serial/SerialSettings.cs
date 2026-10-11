using System.IO.Ports;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace EmbeddedHelper.Core;

public sealed record SerialSettings(string Port = "", int Baud = 115200, int DataBits = 8, Parity Parity = Parity.None, StopBits StopBits = StopBits.One, Handshake FlowControl = Handshake.None);
public sealed record DataBlock(long Sequence, string Direction, byte[] Bytes);
