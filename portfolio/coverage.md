# Eulerian Aurora 기능별 코드 대응

현재 표시되는 포폴의 기술 기여와 메인 프로젝트 최적화 기준입니다. 삭제된 최적화·서브 프로젝트의 숨겨진 최적화는 연결 대상이 아닙니다.

| 구분 | 포폴 항목 | 대표 코드 | 범위 |
| --- | --- | --- | --- |
| 기여 | 퍼즐 핵심 게임플레이 루프 | [gameplay](gameplay/README.md) | 구현 발췌 |
| 기여 | 기믹 시스템과 상호작용 | [gimmick-state](gimmick-state/README.md) · [layer-rotation](layer-rotation/README.md) | 구현 발췌 |
| 기여 | 맵 에디터와 레벨 제작 파이프라인 | [map-generation](map-generation/README.md) | 구현 발췌 |
| 기여 | Supabase 기반 UGC 백엔드 | [authentication](authentication/README.md) · [ugc-backend](ugc-backend/README.md) | 클라이언트·서버 RPC 정의 확인 |
| 기여 | UGC 맵 데이터베이스 구조 설계 | [ugc-backend](ugc-backend/README.md) | Supabase 실구조 확인 · 복합키·RLS 발췌와 ER 도식 |
| 최적화 | RPC 조회 기반 맵 목록 조회 최적화 | [ugc-backend](ugc-backend/README.md) | 페이지 단위 배치·병렬 요청과 서버 LEFT JOIN 확인 · 실측 성능 자료 아님 |

2026-10-17 Supabase의 실제 스키마·RLS·RPC·트리거를 읽기 전용으로 확인했습니다. 운영 환경은 변경하지 않았습니다.
