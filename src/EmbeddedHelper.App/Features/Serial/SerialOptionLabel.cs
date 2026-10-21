using System.Globalization;
using System.IO.Ports;
using System.Windows.Data;

namespace EmbeddedHelper.App;

public sealed class SerialOptionLabel : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Parity.None => "无",
        Parity.Odd => "奇校验",
        Parity.Even => "偶校验",
        Parity.Mark => "标记",
        Parity.Space => "空格",
        StopBits.One => "1",
        StopBits.Two => "2",
        StopBits.OnePointFive => "1.5",
        Handshake.None => "无",
        Handshake.XOnXOff => "软件 XON/XOFF",
        Handshake.RequestToSend => "硬件 RTS/CTS",
        Handshake.RequestToSendXOnXOff => "软硬件结合",
        _ => value.ToString() ?? ""
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
