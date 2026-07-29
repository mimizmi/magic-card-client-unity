package protocol

const (
	MsgPing uint16 = 1 // S→C heartbeat probe
	MsgPong uint16 = 2 // C→S heartbeat response

	MsgLoginReq  uint16 = 1001 // C→S login
	MsgLoginResp uint16 = 1002 // S→C login result

	MsgDamageEv uint16 = 5001 // S→C damage detail
)
