import httpx
import pytest

from resumable_api_batch_extractor.client import ApiContractError, HttpApiClient, RecoverableApiError
from resumable_api_batch_extractor.demo_api import SyntheticPaginatedApi, build_synthetic_records
from resumable_api_batch_extractor.models import ExtractorConfig


def test_client_fetches_page_and_retries_transient_failure() -> None:
    api = SyntheticPaginatedApi(build_synthetic_records(5), transient_failures={0: 1})
    config = ExtractorConfig(page_size=2)

    with HttpApiClient("https://synthetic.local", transport=api.transport()) as client:
        page = client.fetch_page(config, None)

    assert len(page.records) == 2
    assert page.next_cursor == "2"
    assert api.failures_seen[0] == 1
    assert api.requests_seen == [0, 0]


def test_client_raises_after_retry_budget_is_exhausted() -> None:
    api = SyntheticPaginatedApi(build_synthetic_records(5), transient_failures={0: 3})
    config = ExtractorConfig(page_size=2, retry_attempts=2)

    with HttpApiClient("https://synthetic.local", transport=api.transport()) as client:
        with pytest.raises(RecoverableApiError):
            client.fetch_page(config, None)


def test_client_validates_contract() -> None:
    def handler(_request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"items": [{"id": "wrong-field"}]})

    config = ExtractorConfig()
    with HttpApiClient("https://synthetic.local", transport=httpx.MockTransport(handler)) as client:
        with pytest.raises(ApiContractError):
            client.fetch_page(config, None)

