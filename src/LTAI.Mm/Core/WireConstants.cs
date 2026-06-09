namespace LTAI.Mm.Core;

internal static class Prefix
{
    internal const int POSITIVE_INT = 0x00;
    internal const int NEGATIVE_INT = 0x10;
    internal const int SIMPLE = 0x20;
    internal const int FLOAT = 0x30;
    internal const int STRING = 0x40;
    internal const int BYTES = 0x50;
    internal const int CONTAINER = 0x60;
    internal const int TAG = 0x70;

    internal const int MASK = 0xF0;
    internal const int LEN_MASK = 0x0F;
}

internal static class SimpleValue
{
    internal const int FALSE = 0;
    internal const int TRUE = 1;
    internal const int NULL = 2;

    internal static int? NameToValue(string name) => name switch
    {
        "false" => FALSE,
        "true" => TRUE,
        "null" => NULL,
        _ => null,
    };

    internal static string ValueToName(int value) => value switch
    {
        0 => "false",
        1 => "true",
        2 => "null",
        _ => $"unknown({value})",
    };
}

internal static class WireConstants
{
    internal const int INT_LEN_1 = 0;
    internal const int INT_LEN_2 = 1;
    internal const int INT_LEN_3 = 2;
    internal const int INT_LEN_4 = 3;
    internal const int INT_LEN_5 = 4;
    internal const int INT_LEN_6 = 5;
    internal const int INT_LEN_7 = 6;
    internal const int INT_LEN_8 = 7;
    internal const long MAX_1 = 0xFF;
    internal const long MAX_2 = 0xFFFF;
    internal const long MAX_3 = 0xFFFFFF;
    internal const long MAX_4 = 0xFFFFFFFF;
    internal const long MAX_5 = 0xFFFFFFFFFF;
    internal const long MAX_6 = 0xFFFFFFFFFFFF;
    internal const long MAX_7 = 0xFFFFFFFFFFFFFF;

    internal const int STRING_LEN_1 = 0;
    internal const int STRING_LEN_2 = 1;
    internal const int STRING_LEN_3 = 2;
    internal const int STRING_LEN_MAX = 3;

    internal const int BYTES_LEN_1 = 0;
    internal const int BYTES_LEN_2 = 1;
    internal const int BYTES_LEN_3 = 2;

    internal const int CONTAINER_ARRAY = 0x00;
    internal const int CONTAINER_MAP = 0x08;
    internal const int CONTAINER_LEN_1 = 0;
    internal const int CONTAINER_LEN_2 = 1;
    internal const int CONTAINER_LEN_3 = 2;

    internal const int FLOAT_NEG_MASK = 0x08;
    internal const int FLOAT_LEN_1 = 0;
    internal const int FLOAT_LEN_2 = 1;
    internal const int FLOAT_LEN_3 = 2;
    internal const int FLOAT_LEN_4 = 3;
    internal const int FLOAT_LEN_5 = 4;
    internal const int FLOAT_LEN_6 = 5;
    internal const int FLOAT_LEN_7 = 6;
    internal const int FLOAT_LEN_8 = 7;

    internal const int TAG_LEN_1 = 0;

    internal const int MAX_CONTAINER_PAYLOAD = 0xFFFF;
    internal const int MAX_TAG_PAYLOAD = 0xFFFF;
    internal const int MAX_STRING_LEN = 0xFFFF;
    internal const int MAX_BYTES_LEN = 0xFFFF;
}
