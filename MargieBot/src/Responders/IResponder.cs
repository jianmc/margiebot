using System.Threading.Tasks;

namespace MargieBot
{
    public interface IResponder
    {
        bool CanRespond(ResponseContext context);
        Task<BotMessage> GetResponse(ResponseContext context);
    }
}