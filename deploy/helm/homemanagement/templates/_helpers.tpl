{{/*
Common labels applied to all resources.
*/}}
{{- define "homemanagement.labels" -}}
app.kubernetes.io/managed-by: {{ .Release.Service }}
app.kubernetes.io/part-of: homemanagement
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
helm.sh/chart: {{ .Chart.Name }}-{{ .Chart.Version }}
{{- end -}}

{{/*
Selector labels for a given component.
*/}}
{{- define "homemanagement.selectorLabels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .release }}
{{- end -}}

{{/*
Resolve image reference: registry/repository:tag
*/}}
{{- define "homemanagement.image" -}}
{{ .global.imageRegistry }}/{{ .image.repository }}:{{ .image.tag | default .global.imageTag }}
{{- end -}}
