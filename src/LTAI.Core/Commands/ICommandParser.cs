namespace LTAI.Core.Commands;

public interface ICommandParser
{
    Command Parse(string input);
}
