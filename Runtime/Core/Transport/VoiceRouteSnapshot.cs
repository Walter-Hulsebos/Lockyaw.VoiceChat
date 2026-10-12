using System;
using System.Collections.Generic;

namespace Lockyaw.VoiceChat {

    public sealed class VoiceRouteSnapshot {

        public readonly VoiceRouteId RouteId;
        public readonly ulong SourcePeerId;
        public readonly ConversationMembership Membership;
        public readonly bool IsEnabled;
        public readonly int MaxFramesPerSecond;
        public IReadOnlyList<VoiceRouteRecipient> Recipients => recipients;

        private readonly VoiceRouteRecipient[] recipients;

        public VoiceRouteSnapshot(VoiceRouteId routeId, ulong sourcePeerId, ConversationMembership membership, bool isEnabled, int maxFramesPerSecond, VoiceRouteRecipient[] recipients) {
            RouteId = routeId;
            SourcePeerId = sourcePeerId;
            Membership = membership;
            IsEnabled = isEnabled;
            MaxFramesPerSecond = Math.Max(1, Math.Min(maxFramesPerSecond, 120));
            this.recipients = recipients == null ? Array.Empty<VoiceRouteRecipient>() : (VoiceRouteRecipient[])recipients.Clone();
        }

    }

}
