# Camera view acceptance probe

Build the sample and stage it in the exact managed connector lab. Enable the experimental camera view hook and set SEVO_KEEL_CAMERA_VIEW_PROBE=1. Start a disposable solo Creative world without other interaction fixtures.

The sample creates a stock hull, follows and moves it, checks that the physical player and its parent remain in place, and observes native action dispatch. It requires captured item inputs to release while stock blueprint and interaction paths stay blocked. Disposal must restore the native camera and ordinary input before another lease can start. The sample then destroys the target and checks a second restoration.

Success emits KEEL_CAMERA_VIEW FOLLOW PASS and KEEL_CAMERA_VIEW PASS. Failure identifies the phase in KEEL_CAMERA_VIEW FAIL. These checks exercise native pose and action paths. Visual presentation and continuous movement can also be checked during a manual run.
