namespace Quack;

public class QuackException : Exception
{
    public QuackException(string message)
        : base(message)
    {
    }

    public QuackException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
