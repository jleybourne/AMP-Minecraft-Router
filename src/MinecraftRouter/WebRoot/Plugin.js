/// <reference path="..\..\GSMyAdmin\WebRoot\Scripts\UI.js" />

// Shows the dialogs pushed by the plugin (the "Show routes" button).
this.plugin = {
    PushedMessage: function (source, message, parameters) {
        var kinds = {
            routes: ["Minecraft Router", UI.Icons.Info]
        };
        var kind = kinds[message];
        if (!kind) { return; }
        UI.ShowModalAsync(kind[0], parameters, kind[1], UI.OKActionOnly);
        // The modal collapses newlines; pre-line keeps one route per line.
        $("#modalmessage").css("white-space", "pre-line");
    }
};
