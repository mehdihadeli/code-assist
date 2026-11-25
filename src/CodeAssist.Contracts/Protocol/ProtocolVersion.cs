namespace CodeAssist.Contracts.Protocol;

public static class ProtocolVersion
{
    public const int Major = 1;
    public const int Minor = 0;
    public static string Current => $"{Major}.{Minor}";
}
