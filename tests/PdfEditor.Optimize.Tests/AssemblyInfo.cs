// PDFium은 프로세스 안에서 한 번에 한 스레드만 쓸 수 있으므로 테스트를 병렬로 돌리지 않는다.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
