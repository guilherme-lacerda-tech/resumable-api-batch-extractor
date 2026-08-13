# Contributing

This is a public portfolio project, so changes should stay small, reviewable and synthetic.

## Setup

```bash
python -m pip install -e ".[dev]"
```

## Local Checks

```bash
python -m ruff check .
python -m pytest --cov --cov-report=term-missing -q
```

## Pull Requests

- Create a topic branch from `main`.
- Keep examples and tests synthetic.
- Update docs when behavior or commands change.
- Include how the change was tested.
- Do not include real endpoints, credentials, logs, customer data or private architecture.

