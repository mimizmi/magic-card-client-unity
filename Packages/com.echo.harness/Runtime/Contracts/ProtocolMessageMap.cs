using System;
using System.Collections.Generic;

namespace Echo.Harness.Contracts
{
    /// <summary>
    /// Maps each message id to its typed payload.
    ///
    /// Messages whose fixture payload shape is "none" are deliberately absent:
    /// Ping and Pong are sent with a nil payload, and LeaveQueueRequest and
    /// RokkaActivateRequest have no Go struct at all because their handlers
    /// ignore the body. ProtocolDtoContractTests asserts that absence rather
    /// than letting it look like an oversight.
    /// </summary>
    public static class ProtocolMessageMap
    {
        public static IReadOnlyDictionary<MessageId, Type> PayloadTypes { get; } =
            new Dictionary<MessageId, Type>
            {
                { MessageId.ClientPingRequest, typeof(ClientPingRequestDto) },
                { MessageId.ClientPingResponse, typeof(ClientPingResponseDto) },

                { MessageId.LoginRequest, typeof(LoginRequestDto) },
                { MessageId.LoginResponse, typeof(LoginResponseDto) },

                { MessageId.JoinQueueRequest, typeof(JoinQueueRequestDto) },
                { MessageId.JoinQueueResponse, typeof(JoinQueueResponseDto) },
                { MessageId.MatchFoundEvent, typeof(MatchFoundEventDto) },
                { MessageId.SelectCharacterRequest, typeof(SelectCharacterRequestDto) },
                { MessageId.GameStartEvent, typeof(GameStartEventDto) },
                { MessageId.CreateAiGameRequest, typeof(CreateAiGameRequestDto) },

                { MessageId.GameStateEvent, typeof(GameStateEventDto) },
                { MessageId.PhaseChangeEvent, typeof(PhaseChangeEventDto) },

                { MessageId.PlayCardRequest, typeof(PlayCardRequestDto) },
                { MessageId.MoveToSynthesisRequest, typeof(MoveToSynthesisRequestDto) },
                { MessageId.SynthesizeRequest, typeof(SynthesizeRequestDto) },
                { MessageId.UseSkillRequest, typeof(UseSkillRequestDto) },
                { MessageId.TriggerLiberationRequest, typeof(TriggerLiberationRequestDto) },
                { MessageId.EndActionRequest, typeof(EndActionRequestDto) },
                { MessageId.DefenseRequest, typeof(DefenseRequestDto) },
                { MessageId.GameConfigRequest, typeof(GameConfigRequestDto) },
                { MessageId.SurrenderRequest, typeof(SurrenderRequestDto) },
                { MessageId.ReviveRequest, typeof(ReviveRequestDto) },

                { MessageId.DamageEvent, typeof(DamageEventDto) },
                { MessageId.SkillUsedEvent, typeof(SkillUsedEventDto) },
                { MessageId.LiberationEvent, typeof(LiberationEventDto) },
                { MessageId.FieldEffectEvent, typeof(FieldEffectEventDto) },
                { MessageId.PlayerStatusEvent, typeof(PlayerStatusEventDto) },
                { MessageId.GameOverEvent, typeof(GameOverEventDto) },
                { MessageId.ErrorEvent, typeof(ErrorEventDto) },
                { MessageId.BlessingEvent, typeof(BlessingEventDto) },
                { MessageId.IncomingAttackEvent, typeof(IncomingAttackEventDto) },
                { MessageId.TurnTimerEvent, typeof(TurnTimerEventDto) },
                { MessageId.GameConfigEvent, typeof(GameConfigEventDto) },
                { MessageId.CardPlayedEvent, typeof(CardPlayedEventDto) },
                { MessageId.DeathDialogEvent, typeof(DeathDialogEventDto) },
            };

        /// <summary>
        /// Maps each entry of the fixture's "types" dictionary to its DTO. Keys
        /// are the Go type names the extractor emits. These are the structs a
        /// payload field references rather than messages in their own right.
        /// </summary>
        public static IReadOnlyDictionary<string, Type> NestedTypes { get; } =
            new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                { "CardRef", typeof(CardRefDto) },
                { "CardView", typeof(CardViewDto) },
                { "PendingAttackView", typeof(PendingAttackViewDto) },
                { "PlayerView", typeof(PlayerViewDto) },
                { "OpponentView", typeof(OpponentViewDto) },
            };

        /// <summary>
        /// Maps a request to the response the server answers it with.
        ///
        /// The protocol carries no correlation identifier, so waiting for the
        /// next message of the paired id is the only correlation available.
        /// Keeping the three pairs in one table makes that assumption auditable
        /// rather than scattered through call sites.
        /// </summary>
        public static IReadOnlyDictionary<MessageId, MessageId> ResponseFor { get; } =
            new Dictionary<MessageId, MessageId>
            {
                { MessageId.ClientPingRequest, MessageId.ClientPingResponse },
                { MessageId.LoginRequest, MessageId.LoginResponse },
                { MessageId.JoinQueueRequest, MessageId.JoinQueueResponse },
            };
    }
}
