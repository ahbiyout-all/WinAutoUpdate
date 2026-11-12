using Xunit;

// Maestro 마커 테스트는 프로세스 환경 변수(UPDATEAGENT_STATE_PATH)를 사용하므로
// 테스트 간 간섭을 막기 위해 병렬 실행을 비활성화한다.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
