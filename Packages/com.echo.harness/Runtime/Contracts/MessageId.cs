namespace Echo.Harness.Contracts
{
    public enum MessageId : ushort
    {
        Ping = 1,
        Pong = 2,
        ClientPingRequest = 3,
        ClientPingResponse = 4,

        LoginRequest = 1001,
        LoginResponse = 1002,

        JoinQueueRequest = 2001,
        JoinQueueResponse = 2002,
        LeaveQueueRequest = 2003,
        MatchFoundEvent = 2004,
        SelectCharacterRequest = 2005,
        GameStartEvent = 2006,
        CreateAiGameRequest = 2007,

        GameStateEvent = 3001,
        PhaseChangeEvent = 3002,

        PlayCardRequest = 4001,
        MoveToSynthesisRequest = 4002,
        SynthesizeRequest = 4003,
        UseSkillRequest = 4004,
        TriggerLiberationRequest = 4005,
        EndActionRequest = 4006,
        DefenseRequest = 4007,
        GameConfigRequest = 4008,
        SurrenderRequest = 4009,
        ReviveRequest = 4010,
        RokkaActivateRequest = 4011,

        DamageEvent = 5001,
        SkillUsedEvent = 5002,
        LiberationEvent = 5003,
        FieldEffectEvent = 5004,
        PlayerStatusEvent = 5005,
        GameOverEvent = 5006,
        ErrorEvent = 5007,
        BlessingEvent = 5008,
        IncomingAttackEvent = 5009,
        TurnTimerEvent = 5010,
        GameConfigEvent = 5011,
        CardPlayedEvent = 5012,
        DeathDialogEvent = 5013
    }
}
