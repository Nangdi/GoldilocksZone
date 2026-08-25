행성 표면 영상 폴더

PlanetDiveDirector 가 카메라 줌인이 끝난 뒤 재생할 영상을 여기서 읽는다.
단계(D1~D9)에 해당하는 번호 폴더에 영상 파일을 넣으면 된다.

  video/1/  -> D1 에서 재생
  video/2/  -> D2 에서 재생
  ...
  video/9/  -> D9 에서 재생

지원 확장자 : .mp4  .mov  .m4v  .webm  .avi
한 폴더에 여러 개를 넣으면 이름순 첫 번째를 쓴다.
무작위로 고르게 하려면 인스펙터의 pickRandomWhenMultiple 을 켠다.

폴더가 비어 있으면 영상 대신 정지 이미지(surfaceByStep / surfaceByZone)로
넘어가고, 그것도 없으면 어두운 단색을 띄운다.

프로젝트에 임포트하지 않고 이 폴더에서 직접 읽으므로,
빌드한 뒤에도 파일만 갈아 끼우면 영상이 바뀐다.

--- 대기영상 ---

  video/idle/  -> 체험자가 없을 때 반복 재생하는 대기영상

전시 흐름
  대기영상 -> (지구 모형을 움직여 D 신호) -> 인트로 멘트 -> 체험
           -> 영상이 끝나고 30초간 신호 없음 -> 대기영상

인트로 멘트와 대기 복귀 시간은 StreamingAssets/goldilocks.json 에서 바꾼다.
  introMessages        멘트 목록 (줄바꿈은 \n)
  introSecondsPerMessage  멘트 한 줄이 머무는 시간(초)
  introFadeSeconds     멘트가 바뀔 때 흐려졌다 나타나는 시간(초)
  idleTimeoutSeconds   무입력 후 대기영상으로 돌아가기까지의 시간(초)
  idleVideoFolder      대기영상 폴더 이름
