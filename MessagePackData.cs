using MessagePack;
using MessagePack.Resolvers;

namespace FluentBrowser;

internal static class MessagePackData
{
    internal static readonly MessagePackSerializerOptions Options =
        StandardResolver.Options.WithSecurity(
            MessagePackSecurity.UntrustedData);
}
