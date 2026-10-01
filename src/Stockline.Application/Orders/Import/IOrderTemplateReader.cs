namespace Stockline.Application.Orders.Import;

public interface IOrderTemplateReader
{
    OrderTemplate Read(Stream content);
}

public sealed class InvalidOrderTemplateException(string message) : Exception(message);
