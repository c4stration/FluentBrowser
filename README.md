# FluentBrowser
I haven't thought of a name for this one yet, so "FluentBrowser" is just a placeholder for now...

ROADMAP:  
(the items don't need to be sequential)  
✅ Add MenuFlyout context menus  
🚧 Add every option to the new context menu  
✅ Make it unfocus after pressing Enter on the address bar  
✅ Add every navigation option
✅ Add extension support (unsure if possible on WinUI 3)
🚧 Add extension UI/settings
✅ Add support for downloads (download progress, list of previous downloads)
❌ Add privacy-related stuff like hiding your IP (copy from Safari)
❌ Add "inspect element"
✅ Add search suggestions
🚧 Add personalized suggestions 
✅ Add settings
✅ Add fullscreen support
❌ Add toolbar managing
🚧 Add zoom controls (works, but unpolished)
❌ Add a native "Find" feature
❌ Add keyboard shortcuts
✅ Add a context menu for tabs
✅ Reopen closed tab
✅ Duplicate tab
✅ Middle click to close tabs
✅ Add native error pages
✅ Cache favicons
✅ Fix the white FLASHBANG when navigating between tabs
✅ Add a proper determinate progress bar when loading websites

(do these two in order, after doing all of the above)
❌ Polish everything
❌ Optimize everything

[0.21.1]
+ SettingsPage now unsubscribes from MainWindow.FaviconCacheChanged when disposed. That event previously kept every closed Settings page alive
+ Closing a non-WebView tab now clears CurrentTabContent and disposes its page before removing it
