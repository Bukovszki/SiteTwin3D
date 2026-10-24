# Model converter (spec 9.2): IFC (IfcOpenShell, LGPL – used unmodified as a library of this separate program),
# mesh formats (Assimp, BSD), point clouds (laspy + lazrs, pye57/libE57Format, pyproj). Internal service only.

# pye57 has no arm64 wheel: it is built here against Xerces-C.
FROM docker.io/library/python:3.12-slim-trixie AS build
RUN apt-get update -qq && apt-get install -y -qq --no-install-recommends g++ cmake libxerces-c-dev >/dev/null && rm -rf /var/lib/apt/lists/*
# One compiler job: the C++ build needs much memory (parallel compose builds on a small VM).
RUN MAX_JOBS=1 CMAKE_BUILD_PARALLEL_LEVEL=1 pip wheel --quiet --no-deps pye57==0.4.19 -w /wheels

FROM docker.io/library/python:3.12-slim-trixie
RUN apt-get update -qq && apt-get install -y -qq --no-install-recommends assimp-utils libxerces-c3.2t64 >/dev/null && rm -rf /var/lib/apt/lists/*
COPY converter/requirements.txt /app/requirements.txt
COPY --from=build /wheels /wheels
RUN pip install --quiet --no-cache-dir --root-user-action=ignore -r /app/requirements.txt /wheels/*.whl && rm -rf /wheels
COPY converter/*.py /app/
COPY converter/tests /app/tests
WORKDIR /app
RUN useradd --uid 1654 --no-create-home converter
USER converter
ENV PYTHONUNBUFFERED=1 PORT=8090
EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8090/health')"
CMD ["python", "app.py"]
