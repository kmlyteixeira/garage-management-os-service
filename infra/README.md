# OS Service AWS

Provisiona o PostgreSQL privado do OS Service e seu Deployment/Service no EKS
existente.

No Environment `aws-lab` do GitHub, configure as Variables
`AWS_REGION` e `TERRAFORM_ENVIRONMENT`. Configure o Secret
`AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY` e `AWS_SESSION_TOKEN`. O workflow busca automaticamente o EKS e a string
de conexão PostgreSQL no SSM. O `garage-management-database` deve ter sido
aplicado antes.

O state usa o bucket S3 criado automaticamente pelo `infra` e a chave
`os/terraform.tfstate`. O workflow
valida Terraform em PRs e aplica somente no `master`. Não versionar senhas ou
arquivos `.tfvars` reais.
