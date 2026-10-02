using MessagePack;
using System.Collections.Generic;

namespace FluentBrowser;

[MessagePackObject]
public sealed class BrowserSession
{
    [Key(0)]
    public List<string> TabUrls { get; set; } = [];

    [Key(1)]
    public int SelectedTabIndex { get; set; }
}