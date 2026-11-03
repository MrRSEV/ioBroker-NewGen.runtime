namespace ioBroker.Core.Security
{
    public interface ISecurityEventPublisher
    {
        void Publish(object sender, SecurityEventArgs args);
    }

    internal sealed class NullSecurityEventPublisher : ISecurityEventPublisher
    {
        public static readonly NullSecurityEventPublisher Instance = new();

        public void Publish(object sender, SecurityEventArgs args)
        {
        }
    }
}
