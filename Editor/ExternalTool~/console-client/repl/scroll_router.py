WHEEL_TARGET_TRANSCRIPT = "transcript"
WHEEL_TARGET_COMPLETION = "completion"


def resolve_wheel_target(*, completion_open):
    if completion_open:
        return WHEEL_TARGET_COMPLETION
    return WHEEL_TARGET_TRANSCRIPT
