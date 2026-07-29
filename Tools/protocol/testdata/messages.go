package protocol

type LoginReq struct {
	PlayerName     string `json:"player_name"`
	ReconnectToken string `json:"reconnect_token,omitempty"`
}

type EndActionReq struct{}

type ReviveReq struct {
	Card1 CardRef `json:"card1"`
	Card2 CardRef `json:"card2"`
}

type CardRef struct {
	Zone string `json:"zone"`
	Slot int    `json:"slot"`
}

type GameConfigEv struct {
	Characters []map[string]any `json:"characters"`
	ConfigHash string           `json:"config_hash"`
}
