# Reddit Draft

I built a small `micro meet` layer for remote endpoint operations.

The idea is simple:
- pick a device from a fleet view
- open a room
- invite another operator
- hand off control
- keep a timeline of what happened

What I wanted was not another meeting UI and not another raw remote desktop.
I wanted a room model for real endpoint operations.

The part I find interesting is that the room has explicit operator semantics:
- `viewer / moderator / admin`
- invite / approve / revoke flow
- active controller lease
- room timeline

So instead of "someone is connected somehow", the room can answer:
- who can watch
- who can take control
- who granted it
- what changed in the room

Under the hood the current local stack includes:
- bearer-first auth with Keycloak
- session/room orchestration
- policy governance
- local `doctor`, `full`, and `smoke-bearer` scripts so the stack is reproducible

Current demo path:
1. start the local stack
2. open fleet overview
3. launch a room
4. invite a second operator
5. accept/approve the invite
6. hand off control
7. show the room timeline updating

If there is interest, I can publish the cleaned-up repo and write up the control-handoff model in more detail.
