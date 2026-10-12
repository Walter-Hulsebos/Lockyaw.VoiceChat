using WebSocketSharp;
using WebSocketSharp.Server;

namespace Lockyaw.VoiceChat {

    public sealed class WebSocketVoiceBehavior : WebSocketBehavior {

        internal WebSocketVoiceCarrier Carrier;

        protected override void OnMessage(MessageEventArgs e) {
            if (Carrier != null && e.IsBinary) { Carrier.Enqueue(Context.WebSocket, e.RawData); }
        }

        protected override void OnError(ErrorEventArgs e) {
            if (Carrier != null) { Carrier.SetFailure(e.Message); }
        }

    }

}
