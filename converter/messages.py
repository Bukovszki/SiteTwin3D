"""Errors and warnings for the API and the user: a machine code with optional details, `code` or
`code:detail|detail`; the user interface shows them in its own language (models.messages)."""


def msg(code: str, *details) -> str:
    return code + (":" + "|".join(str(d) for d in details) if details else "")
